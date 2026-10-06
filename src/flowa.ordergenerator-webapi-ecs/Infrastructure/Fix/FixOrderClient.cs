using System.Collections.Concurrent;
using Flowa.Commons.Logging;
using Flowa.OrderGenerator.Application.Orders.Interfaces;
using Flowa.OrderGenerator.Domain.Orders.Enums;
using Flowa.OrderGenerator.Domain.Orders.ValueObjects;
using Flowa.OrderGenerator.Infrastructure.Orders.Options;
using Microsoft.Extensions.Options;
using QuickFix;
using QuickFix.Fields;
using QuickFix.Store;
using QuickFix.Transport;
using FixSide = QuickFix.Fields.Side;

namespace Flowa.OrderGenerator.Infrastructure.Fix;

internal sealed class FixOrderClient : IOrderAccumulatorPort, IApplication, IHostedService, IDisposable
{
    public const string SessionRejectWithoutReasonText = "A sessão FIX recusou a ordem.";
    public const string LateExecutionReportLogMessage = "ExecutionReport arrived for an order that is no longer waiting for it.";

    private readonly ConcurrentDictionary<string, OrderAwaitingExecutionReport> _ordersAwaitingExecutionReport = new();
    private readonly SocketInitiator _fixSocketInitiator;
    private readonly TimeSpan _executionReportTimeout;
    private readonly IApplicationLogger<FixOrderClient> _fixOrderLogger;
    private SessionID? _initiatorSessionId;

    public FixOrderClient(IOptions<FixOptions> fixOptions, FixSessionLogFactory fixSessionLogFactory, IApplicationLogger<FixOrderClient> fixOrderLogger)
    {
        var receivedFixOptions = fixOptions ?? throw new ArgumentNullException(nameof(fixOptions));
        var receivedFixSessionLogFactory = fixSessionLogFactory ?? throw new ArgumentNullException(nameof(fixSessionLogFactory));
        _fixOrderLogger = fixOrderLogger ?? throw new ArgumentNullException(nameof(fixOrderLogger));
        _executionReportTimeout = TimeSpan.FromSeconds(receivedFixOptions.Value.ExecutionReportTimeoutSeconds);
        _fixSocketInitiator = new SocketInitiator(
            this, new MemoryStoreFactory(), LoadInitiatorSessionSettings(receivedFixOptions.Value), receivedFixSessionLogFactory, null);
    }

    internal int OrdersAwaitingExecutionReportCount => _ordersAwaitingExecutionReport.Count;

    public async Task<SentOrderResult> SendOrderAsync(OrderToSend orderToSend)
    {
        using var orderSending = FixOrderTraceProvider.StartOrderSending();
        var clOrdId = FixOrderTraceProvider.CreateClOrdId(orderSending);

        return await SendNewOrderSingleAndWaitForExecutionReportAsync(
            clOrdId, orderToSend, FixOrderTraceProvider.GetTraceParentOfOrderSending(orderSending));
    }

    private async Task<SentOrderResult> SendNewOrderSingleAndWaitForExecutionReportAsync(string clOrdId, OrderToSend orderToSend, string? orderSendingTraceParent)
    {
        var initiatorSessionId = _initiatorSessionId;
        var initiatorSession = initiatorSessionId is null ? null : Session.LookupSession(initiatorSessionId);
        if (initiatorSessionId is null || initiatorSession is null || !initiatorSession.IsLoggedOn)
            return new SentOrderResult(SentOrderStatus.NoLoggedOnSession, clOrdId);

        var orderAwaitingExecutionReport = new OrderAwaitingExecutionReport();
        var orderAnswerWaiter = orderAwaitingExecutionReport.OrderAnswer;
        _ordersAwaitingExecutionReport[clOrdId] = orderAwaitingExecutionReport;
        try
        {
            if (!Session.SendToTarget(BuildNewOrderSingle(clOrdId, orderToSend, orderSendingTraceParent), initiatorSessionId))
                return new SentOrderResult(SentOrderStatus.NoLoggedOnSession, clOrdId);

            using var executionReportDeadline = new CancellationTokenSource();
            var orderAnswerOrDeadlineFinishedFirst = await Task.WhenAny(orderAnswerWaiter.Task, Task.Delay(_executionReportTimeout, executionReportDeadline.Token));
            if (orderAnswerOrDeadlineFinishedFirst != orderAnswerWaiter.Task)
                return new SentOrderResult(SentOrderStatus.ExecutionReportTimeout, clOrdId);

            await executionReportDeadline.CancelAsync();
            return await orderAnswerWaiter.Task;
        }
        finally
        {
            _ordersAwaitingExecutionReport.TryRemove(clOrdId, out _);
        }
    }

    private static SessionSettings LoadInitiatorSessionSettings(FixOptions fixOptions)
    {
        var initiatorSettings = new SessionSettings(Path.Combine(AppContext.BaseDirectory, "initiator.cfg"));

        foreach (var configuredSessionId in initiatorSettings.GetSessions())
        {
            var configuredSession = initiatorSettings.Get(configuredSessionId);
            configuredSession.SetString(SessionSettings.SOCKET_CONNECT_HOST, fixOptions.AcceptorHost);
            configuredSession.SetLong(SessionSettings.SOCKET_CONNECT_PORT, fixOptions.AcceptorPort);
            configuredSession.SetString(SessionSettings.DATA_DICTIONARY, Path.Combine(AppContext.BaseDirectory, "FIX44-flowa.xml"));
        }

        return initiatorSettings;
    }

    private static QuickFix.FIX44.NewOrderSingle BuildNewOrderSingle(string clOrdId, OrderToSend orderToSend, string? orderSendingTraceParent)
    {
        var newOrderSingle = new QuickFix.FIX44.NewOrderSingle(
            new ClOrdID(clOrdId),
            new Symbol(orderToSend.Symbol),
            new FixSide(ConvertToFixSide(orderToSend.Side)),
            new TransactTime(DateTime.UtcNow),
            new OrdType(OrdType.LIMIT));
        newOrderSingle.Set(new OrderQty(orderToSend.Quantity));
        newOrderSingle.Set(new Price(orderToSend.Price));
        if (orderSendingTraceParent is not null)
            newOrderSingle.SetField(new StringField(FixOrderTraceProvider.TraceParentTag, orderSendingTraceParent));
        return newOrderSingle;
    }

    private static char ConvertToFixSide(OrderSide orderSide) => orderSide switch
    {
        OrderSide.Buy => FixSide.BUY,
        OrderSide.Sell => FixSide.SELL,
        _ => throw new ArgumentOutOfRangeException(nameof(orderSide), orderSide, "The order side has no FIX side.")
    };

    private static SentOrderResult ConvertToSentOrderResult(string clOrdId, Message executionReport)
    {
        var orderId = executionReport.GetString(Tags.OrderID);
        var execId = executionReport.GetString(Tags.ExecID);

        return executionReport.GetChar(Tags.ExecType) switch
        {
            ExecType.NEW => new SentOrderResult(SentOrderStatus.Accepted, clOrdId, orderId, execId),
            ExecType.REJECTED => new SentOrderResult(SentOrderStatus.Rejected, clOrdId, orderId, execId,
                executionReport.IsSetField(Tags.Text) ? executionReport.GetString(Tags.Text) : null),
            _ => new SentOrderResult(SentOrderStatus.UnexpectedExecutionReport, clOrdId, orderId, execId)
        };
    }

    private void AnswerOrderWithExecutionReport(Message executionReport)
    {
        if (!executionReport.IsSetField(Tags.ClOrdID))
            return;

        var clOrdId = executionReport.GetString(Tags.ClOrdID);
        if (_ordersAwaitingExecutionReport.TryGetValue(clOrdId, out var answeredOrder))
            answeredOrder.OrderAnswer.TrySetResult(ConvertToSentOrderResult(clOrdId, executionReport));
        else
            _fixOrderLogger.LogWarning(LateExecutionReportLogMessage, new { ClOrdId = clOrdId, TraceId = clOrdId });
    }

    private void AnswerOrderWithBusinessMessageReject(Message businessMessageReject)
    {
        var rejectedClOrdId = businessMessageReject.IsSetField(Tags.BusinessRejectRefID)
            ? businessMessageReject.GetString(Tags.BusinessRejectRefID)
            : FindClOrdIdOfRejectedSeqNum(businessMessageReject);
        var businessRejectText = businessMessageReject.IsSetField(Tags.Text)
            ? businessMessageReject.GetString(Tags.Text)
            : $"O OrderAccumulator recusou a ordem (BusinessRejectReason {businessMessageReject.GetInt(Tags.BusinessRejectReason)}).";

        AnswerOrderWithFixReject(rejectedClOrdId, businessRejectText);
    }

    private void AnswerOrderWithSessionReject(Message sessionReject)
    {
        var sessionRejectText = sessionReject.IsSetField(Tags.Text)
            ? sessionReject.GetString(Tags.Text)
            : sessionReject.IsSetField(Tags.SessionRejectReason)
                ? $"A sessão FIX recusou a ordem (SessionRejectReason {sessionReject.GetInt(Tags.SessionRejectReason)})."
                : SessionRejectWithoutReasonText;

        AnswerOrderWithFixReject(FindClOrdIdOfRejectedSeqNum(sessionReject), sessionRejectText);
    }

    private string? FindClOrdIdOfRejectedSeqNum(Message fixReject)
    {
        if (!fixReject.IsSetField(Tags.RefSeqNum))
            return null;

        var rejectedSeqNum = fixReject.GetULong(Tags.RefSeqNum);
        return _ordersAwaitingExecutionReport.FirstOrDefault(awaitingOrder => awaitingOrder.Value.WasSentWithSeqNum(rejectedSeqNum)).Key;
    }

    private void AnswerOrderWithFixReject(string? rejectedClOrdId, string fixRejectText)
    {
        if (rejectedClOrdId is not null && _ordersAwaitingExecutionReport.TryGetValue(rejectedClOrdId, out var rejectedOrder))
            rejectedOrder.OrderAnswer.TrySetResult(new SentOrderResult(SentOrderStatus.RejectedByFixReject, rejectedClOrdId, rejectionText: fixRejectText));
    }

    private void EndEveryOrderAwaitingExecutionReportAsSessionLost()
    {
        foreach (var (clOrdId, orderLeftWithoutSession) in _ordersAwaitingExecutionReport)
            orderLeftWithoutSession.OrderAnswer.TrySetResult(new SentOrderResult(SentOrderStatus.FixSessionLost, clOrdId));
    }

    public void FromApp(Message incomingFixApplicationMessage, SessionID orderAccumulatorFixSessionId)
    {
        var incomingMessageType = incomingFixApplicationMessage.Header.GetString(Tags.MsgType);
        if (incomingMessageType == MsgType.EXECUTION_REPORT)
            AnswerOrderWithExecutionReport(incomingFixApplicationMessage);
        else if (incomingMessageType == MsgType.BUSINESS_MESSAGE_REJECT)
            AnswerOrderWithBusinessMessageReject(incomingFixApplicationMessage);
    }

    public void OnCreate(SessionID orderAccumulatorFixSessionId) => _initiatorSessionId = orderAccumulatorFixSessionId;

    public void OnLogon(SessionID orderAccumulatorFixSessionId) { }

    public void OnLogout(SessionID orderAccumulatorFixSessionId) => EndEveryOrderAwaitingExecutionReportAsSessionLost();

    public void ToAdmin(Message outgoingFixAdminMessage, SessionID orderAccumulatorFixSessionId) { }

    public void FromAdmin(Message incomingFixAdminMessage, SessionID orderAccumulatorFixSessionId)
    {
        if (incomingFixAdminMessage.Header.GetString(Tags.MsgType) == MsgType.REJECT)
            AnswerOrderWithSessionReject(incomingFixAdminMessage);
    }

    public void ToApp(Message outgoingFixApplicationMessage, SessionID orderAccumulatorFixSessionId)
    {
        if (!outgoingFixApplicationMessage.IsSetField(Tags.ClOrdID))
            return;

        var sentClOrdId = outgoingFixApplicationMessage.GetString(Tags.ClOrdID);
        if (_ordersAwaitingExecutionReport.TryGetValue(sentClOrdId, out var sentOrder))
            sentOrder.RememberSentSeqNum(outgoingFixApplicationMessage.Header.GetULong(Tags.MsgSeqNum));
    }

    public Task StartAsync(CancellationToken hostStartCancellation)
    {
        _fixSocketInitiator.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken hostStopCancellation)
    {
        _fixSocketInitiator.Stop();
        return Task.CompletedTask;
    }

    public void Dispose() => _fixSocketInitiator.Dispose();

    private sealed class OrderAwaitingExecutionReport
    {
        private const long NotSentYet = -1;

        private long _sentSeqNum = NotSentYet;

        public TaskCompletionSource<SentOrderResult> OrderAnswer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void RememberSentSeqNum(ulong sentSeqNum) => Volatile.Write(ref _sentSeqNum, (long)sentSeqNum);

        public bool WasSentWithSeqNum(ulong rejectedSeqNum) => Volatile.Read(ref _sentSeqNum) == (long)rejectedSeqNum;
    }
}
