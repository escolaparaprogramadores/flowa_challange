using System.Collections.Concurrent;
using Base.OrderGenerator.Application.Orders.Interfaces;
using Base.OrderGenerator.Domain.Orders.Enums;
using Base.OrderGenerator.Domain.Orders.ValueObjects;
using Base.OrderGenerator.Infrastructure.Orders.Options;
using Microsoft.Extensions.Options;
using QuickFix;
using QuickFix.Fields;
using QuickFix.Store;
using QuickFix.Transport;
using FixSide = QuickFix.Fields.Side;

namespace Base.OrderGenerator.Infrastructure.Fix;

internal sealed class FixOrderClient : IOrderAccumulatorPort, IApplication, IHostedService, IDisposable
{
    public static readonly TimeSpan ExecutionReportTimeout = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, TaskCompletionSource<Message>> _ordersAwaitingExecutionReport = new();
    private readonly SocketInitiator _fixSocketInitiator;
    private SessionID? _initiatorSessionId;

    public FixOrderClient(IOptions<FixOptions> fixOptions, FixSessionLogFactory fixSessionLogFactory)
    {
        var receivedFixOptions = fixOptions ?? throw new ArgumentNullException(nameof(fixOptions));
        var receivedFixSessionLogFactory = fixSessionLogFactory ?? throw new ArgumentNullException(nameof(fixSessionLogFactory));
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

        var executionReportWaiter = new TaskCompletionSource<Message>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ordersAwaitingExecutionReport[clOrdId] = executionReportWaiter;
        try
        {
            if (!Session.SendToTarget(BuildNewOrderSingle(clOrdId, orderToSend, orderSendingTraceParent), initiatorSessionId))
                return new SentOrderResult(SentOrderStatus.NoLoggedOnSession, clOrdId);

            using var executionReportDeadline = new CancellationTokenSource();
            var executionReportOrDeadlineFinishedFirst = await Task.WhenAny(executionReportWaiter.Task, Task.Delay(ExecutionReportTimeout, executionReportDeadline.Token));
            if (executionReportOrDeadlineFinishedFirst != executionReportWaiter.Task)
                return new SentOrderResult(SentOrderStatus.ExecutionReportTimeout, clOrdId);

            await executionReportDeadline.CancelAsync();
            return ConvertToSentOrderResult(clOrdId, await executionReportWaiter.Task);
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

    public void FromApp(Message incomingFixApplicationMessage, SessionID orderAccumulatorFixSessionId)
    {
        if (incomingFixApplicationMessage.Header.GetString(Tags.MsgType) != MsgType.EXECUTION_REPORT || !incomingFixApplicationMessage.IsSetField(Tags.ClOrdID))
            return;

        if (_ordersAwaitingExecutionReport.TryGetValue(incomingFixApplicationMessage.GetString(Tags.ClOrdID), out var executionReportWaiter))
            executionReportWaiter.TrySetResult(incomingFixApplicationMessage);
    }

    public void OnCreate(SessionID orderAccumulatorFixSessionId) => _initiatorSessionId = orderAccumulatorFixSessionId;

    public void OnLogon(SessionID orderAccumulatorFixSessionId) { }

    public void OnLogout(SessionID orderAccumulatorFixSessionId) { }

    public void ToAdmin(Message outgoingFixAdminMessage, SessionID orderAccumulatorFixSessionId) { }

    public void FromAdmin(Message incomingFixAdminMessage, SessionID orderAccumulatorFixSessionId) { }

    public void ToApp(Message outgoingFixApplicationMessage, SessionID orderAccumulatorFixSessionId) { }

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
}
