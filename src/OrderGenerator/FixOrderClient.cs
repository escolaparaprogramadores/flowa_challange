using System.Collections.Concurrent;
using Flowa.Shared;
using QuickFix;
using QuickFix.Fields;
using QuickFix.Logger;
using QuickFix.Store;
using QuickFix.Transport;
using FixSide = QuickFix.Fields.Side;

namespace OrderGenerator;

public enum OrderOutcome
{
    Accepted,
    Rejected,
    NoLoggedOnSession,
    ExecutionReportTimeout,
    UnexpectedExecutionReport
}

public sealed record OrderResult(OrderOutcome Outcome, string ClOrdId, string? OrderId = null, string? ExecId = null, string? RejectionText = null);

// Ponta initiator da sessão FIX: manda a NewOrderSingle e espera o ExecutionReport do mesmo ClOrdID.
public sealed class FixOrderClient : IApplication, IHostedService, IDisposable
{
    public static readonly TimeSpan ExecutionReportTimeout = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, TaskCompletionSource<Message>> _ordersAwaitingExecutionReport = new();
    private readonly SocketInitiator _fixSocketInitiator;
    private SessionID? _initiatorSessionId;

    public FixOrderClient(IConfiguration orderGeneratorConfiguration)
    {
        var initiatorSettings = LoadInitiatorSessionSettings(orderGeneratorConfiguration);
        _fixSocketInitiator = new SocketInitiator(this, new MemoryStoreFactory(), initiatorSettings, new ScreenLogFactory(initiatorSettings), null);
    }

    internal int OrdersAwaitingExecutionReportCount => _ordersAwaitingExecutionReport.Count;

    public async Task<OrderResult> SendNewOrderSingleAsync(ValidOrder order)
    {
        var clOrdId = Guid.NewGuid().ToString("N");

        // Sem sessão logada a ordem não sai: o QuickFIX a guardaria na store e mandaria depois do logon (D-34).
        var initiatorSessionId = _initiatorSessionId;
        var initiatorSession = initiatorSessionId is null ? null : Session.LookupSession(initiatorSessionId);
        if (initiatorSessionId is null || initiatorSession is null || !initiatorSession.IsLoggedOn)
            return new OrderResult(OrderOutcome.NoLoggedOnSession, clOrdId);

        // A espera é registrada antes do envio porque a resposta pode chegar antes do Send voltar.
        var executionReportWaiter = new TaskCompletionSource<Message>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ordersAwaitingExecutionReport[clOrdId] = executionReportWaiter;
        try
        {
            if (!Session.SendToTarget(BuildNewOrderSingle(clOrdId, order), initiatorSessionId))
                return new OrderResult(OrderOutcome.NoLoggedOnSession, clOrdId);

            return ToOrderResult(clOrdId, await executionReportWaiter.Task.WaitAsync(ExecutionReportTimeout));
        }
        catch (TimeoutException)
        {
            return new OrderResult(OrderOutcome.ExecutionReportTimeout, clOrdId);
        }
        finally
        {
            _ordersAwaitingExecutionReport.TryRemove(clOrdId, out _);
        }
    }

    private static SessionSettings LoadInitiatorSessionSettings(IConfiguration orderGeneratorConfiguration)
    {
        var initiatorSettings = new SessionSettings(Path.Combine(AppContext.BaseDirectory, "initiator.cfg"));
        var acceptorHost = orderGeneratorConfiguration["Fix:AcceptorHost"]
            ?? throw new InvalidOperationException("Configuração Fix:AcceptorHost ausente.");
        var acceptorPort = orderGeneratorConfiguration.GetValue<int?>("Fix:AcceptorPort")
            ?? throw new InvalidOperationException("Configuração Fix:AcceptorPort ausente.");

        foreach (var configuredSessionId in initiatorSettings.GetSessions())
        {
            var configuredSession = initiatorSettings.Get(configuredSessionId);
            configuredSession.SetString(SessionSettings.SOCKET_CONNECT_HOST, acceptorHost);
            configuredSession.SetLong(SessionSettings.SOCKET_CONNECT_PORT, acceptorPort);
            // Caminho absoluto: o processo pode subir de qualquer pasta.
            configuredSession.SetString(SessionSettings.DATA_DICTIONARY, Path.Combine(AppContext.BaseDirectory, "FIX44.xml"));
        }

        return initiatorSettings;
    }

    private static QuickFix.FIX44.NewOrderSingle BuildNewOrderSingle(string clOrdId, ValidOrder order)
    {
        var newOrderSingle = new QuickFix.FIX44.NewOrderSingle(
            new ClOrdID(clOrdId),
            new Symbol(order.OrderSymbol),
            new FixSide(order.OrderSide.ToFixOrderSide()),
            new TransactTime(DateTime.UtcNow),
            new OrdType(OrdType.LIMIT));
        newOrderSingle.Set(new OrderQty(order.OrderQuantity));
        newOrderSingle.Set(new Price(order.OrderPrice));
        return newOrderSingle;
    }

    private static OrderResult ToOrderResult(string clOrdId, Message executionReport)
    {
        var orderId = executionReport.GetString(Tags.OrderID);
        var execId = executionReport.GetString(Tags.ExecID);

        return executionReport.GetChar(Tags.ExecType) switch
        {
            ExecType.NEW => new OrderResult(OrderOutcome.Accepted, clOrdId, orderId, execId),
            ExecType.REJECTED => new OrderResult(OrderOutcome.Rejected, clOrdId, orderId, execId,
                executionReport.IsSetField(Tags.Text) ? executionReport.GetString(Tags.Text) : null),
            _ => new OrderResult(OrderOutcome.UnexpectedExecutionReport, clOrdId, orderId, execId)
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
