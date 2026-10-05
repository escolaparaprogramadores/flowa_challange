using System.Collections.Concurrent;
using Base.OrderGenerator.Application.Orders.SendOrder;
using Base.OrderGenerator.Commons;
using Base.OrderGenerator.Domain.Orders;
using Base.OrderGenerator.Infrastructure.Fix;
using QuickFix;
using QuickFix.Fields;
using QuickFix.Store;
using QuickFix.Transport;
using FixSide = QuickFix.Fields.Side;

namespace Base.OrderGenerator.Infrastructure;

// Ponta initiator da sessão FIX: manda a NewOrderSingle e espera o ExecutionReport do mesmo ClOrdID.
public sealed class FixOrderClient : IOrderAccumulatorPort, IApplication, IHostedService, IDisposable
{
    public static readonly TimeSpan ExecutionReportTimeout = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, TaskCompletionSource<Message>> _ordersAwaitingExecutionReport = new();
    private readonly SocketInitiator _fixSocketInitiator;
    private SessionID? _initiatorSessionId;

    public FixOrderClient(IConfiguration orderGeneratorConfiguration, FixSessionLogFactory fixSessionLogFactory)
    {
        _fixSocketInitiator = new SocketInitiator(
            this, new MemoryStoreFactory(), LoadInitiatorSessionSettings(orderGeneratorConfiguration), fixSessionLogFactory, null);
    }

    internal int OrdersAwaitingExecutionReportCount => _ordersAwaitingExecutionReport.Count;

    public async Task<SentOrderResult> SendOrderAsync(OrderToSend orderToSend)
    {
        // The span opens before the ClOrdID because the ClOrdID is its trace id; the order logs run inside it. A failed
        // order is logged once, by the GlobalErrorHandler, in the request span that is the parent of this one.
        using var orderSending = FixOrderTraceProvider.StartOrderSending();
        var clOrdId = FixOrderTraceProvider.CreateClOrdId(orderSending);

        return await SendNewOrderSingleAndWaitForExecutionReportAsync(
            clOrdId, orderToSend, FixOrderTraceProvider.GetTraceParentOfOrderSending(orderSending));
    }

    private async Task<SentOrderResult> SendNewOrderSingleAndWaitForExecutionReportAsync(string clOrdId, OrderToSend orderToSend, string? orderSendingTraceParent)
    {
        // Sem sessão logada a ordem não sai: o QuickFIX a guardaria na store e mandaria depois do logon (D-34).
        var initiatorSessionId = _initiatorSessionId;
        var initiatorSession = initiatorSessionId is null ? null : Session.LookupSession(initiatorSessionId);
        if (initiatorSessionId is null || initiatorSession is null || !initiatorSession.IsLoggedOn)
            return new SentOrderResult(SentOrderStatus.NoLoggedOnSession, clOrdId);

        // A espera é registrada antes do envio porque a resposta pode chegar antes do Send voltar.
        var executionReportWaiter = new TaskCompletionSource<Message>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ordersAwaitingExecutionReport[clOrdId] = executionReportWaiter;
        try
        {
            if (!Session.SendToTarget(BuildNewOrderSingle(clOrdId, orderToSend, orderSendingTraceParent), initiatorSessionId))
                return new SentOrderResult(SentOrderStatus.NoLoggedOnSession, clOrdId);

            // No answer in 5 s is an expected outcome, not an error: it becomes a status here and the 503 at the edge.
            using var executionReportDeadline = new CancellationTokenSource();
            var firstToFinish = await Task.WhenAny(executionReportWaiter.Task, Task.Delay(ExecutionReportTimeout, executionReportDeadline.Token));
            if (firstToFinish != executionReportWaiter.Task)
                return new SentOrderResult(SentOrderStatus.ExecutionReportTimeout, clOrdId);

            await executionReportDeadline.CancelAsync();
            return ToSentOrderResult(clOrdId, await executionReportWaiter.Task);
        }
        finally
        {
            _ordersAwaitingExecutionReport.TryRemove(clOrdId, out _);
        }
    }

    private static SessionSettings LoadInitiatorSessionSettings(IConfiguration orderGeneratorConfiguration)
    {
        var initiatorSettings = new SessionSettings(Path.Combine(AppContext.BaseDirectory, "initiator.cfg"));
        var acceptorHost = orderGeneratorConfiguration[OrderGeneratorConfigurationKeys.FixAcceptorHost]
            ?? throw new InvalidOperationException("Configuração Fix:AcceptorHost ausente.");
        var acceptorPort = orderGeneratorConfiguration.GetValue<int?>(OrderGeneratorConfigurationKeys.FixAcceptorPort)
            ?? throw new InvalidOperationException("Configuração Fix:AcceptorPort ausente.");

        foreach (var configuredSessionId in initiatorSettings.GetSessions())
        {
            var configuredSession = initiatorSettings.Get(configuredSessionId);
            configuredSession.SetString(SessionSettings.SOCKET_CONNECT_HOST, acceptorHost);
            configuredSession.SetLong(SessionSettings.SOCKET_CONNECT_PORT, acceptorPort);
            // Caminho absoluto: o processo pode subir de qualquer pasta.
            configuredSession.SetString(SessionSettings.DATA_DICTIONARY, Path.Combine(AppContext.BaseDirectory, "FIX44-flowa.xml"));
        }

        return initiatorSettings;
    }

    private static QuickFix.FIX44.NewOrderSingle BuildNewOrderSingle(string clOrdId, OrderToSend orderToSend, string? orderSendingTraceParent)
    {
        var newOrderSingle = new QuickFix.FIX44.NewOrderSingle(
            new ClOrdID(clOrdId),
            new Symbol(orderToSend.Symbol),
            new FixSide(orderToSend.Side == OrderSide.Buy ? FixSide.BUY : FixSide.SELL),
            new TransactTime(DateTime.UtcNow),
            new OrdType(OrdType.LIMIT));
        newOrderSingle.Set(new OrderQty(orderToSend.Quantity));
        newOrderSingle.Set(new Price(orderToSend.Price));
        if (orderSendingTraceParent is not null)
            newOrderSingle.SetField(new StringField(FixOrderTraceProvider.TraceParentTag, orderSendingTraceParent));
        return newOrderSingle;
    }

    private static SentOrderResult ToSentOrderResult(string clOrdId, Message executionReport)
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

        // Resposta que chega depois dos 5 s não acha mais quem esperava e é descartada.
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
