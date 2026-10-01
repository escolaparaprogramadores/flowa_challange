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
    NoSession,
    Timeout,
    UnexpectedReport
}

public sealed record OrderResult(OrderOutcome Outcome, string ClOrdId, string? OrderId = null, string? ExecId = null, string? Text = null);

// Ponta initiator da sessão FIX: manda a NewOrderSingle e espera o ExecutionReport do mesmo ClOrdID.
public sealed class FixOrderClient : IApplication, IHostedService, IDisposable
{
    public static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, TaskCompletionSource<Message>> _pending = new();
    private readonly SocketInitiator _initiator;
    private SessionID? _sessionId;

    public FixOrderClient(IConfiguration configuration)
    {
        var settings = LoadSettings(configuration);
        _initiator = new SocketInitiator(this, new MemoryStoreFactory(), settings, new ScreenLogFactory(settings), null);
    }

    internal int PendingCount => _pending.Count;

    public async Task<OrderResult> SendAsync(ValidOrder order)
    {
        var clOrdId = Guid.NewGuid().ToString("N");

        // Sem sessão logada a ordem não sai: o QuickFIX a guardaria na store e mandaria depois do logon (D-34).
        var sessionId = _sessionId;
        var session = sessionId is null ? null : Session.LookupSession(sessionId);
        if (sessionId is null || session is null || !session.IsLoggedOn)
            return new OrderResult(OrderOutcome.NoSession, clOrdId);

        // A espera é registrada antes do envio porque a resposta pode chegar antes do Send voltar.
        var waiter = new TaskCompletionSource<Message>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[clOrdId] = waiter;
        try
        {
            if (!Session.SendToTarget(BuildNewOrderSingle(clOrdId, order), sessionId))
                return new OrderResult(OrderOutcome.NoSession, clOrdId);

            return ToResult(clOrdId, await waiter.Task.WaitAsync(ResponseTimeout));
        }
        catch (TimeoutException)
        {
            return new OrderResult(OrderOutcome.Timeout, clOrdId);
        }
        finally
        {
            _pending.TryRemove(clOrdId, out _);
        }
    }

    private static SessionSettings LoadSettings(IConfiguration configuration)
    {
        var settings = new SessionSettings(Path.Combine(AppContext.BaseDirectory, "initiator.cfg"));
        var host = configuration["Fix:AcceptorHost"]
            ?? throw new InvalidOperationException("Configuração Fix:AcceptorHost ausente.");
        var port = configuration.GetValue<int?>("Fix:AcceptorPort")
            ?? throw new InvalidOperationException("Configuração Fix:AcceptorPort ausente.");

        foreach (var sessionId in settings.GetSessions())
        {
            var session = settings.Get(sessionId);
            session.SetString(SessionSettings.SOCKET_CONNECT_HOST, host);
            session.SetLong(SessionSettings.SOCKET_CONNECT_PORT, port);
            // Caminho absoluto: o processo pode subir de qualquer pasta.
            session.SetString(SessionSettings.DATA_DICTIONARY, Path.Combine(AppContext.BaseDirectory, "FIX44.xml"));
        }

        return settings;
    }

    private static QuickFix.FIX44.NewOrderSingle BuildNewOrderSingle(string clOrdId, ValidOrder order)
    {
        var message = new QuickFix.FIX44.NewOrderSingle(
            new ClOrdID(clOrdId),
            new Symbol(order.Symbol),
            new FixSide(order.Side.ToFix()),
            new TransactTime(DateTime.UtcNow),
            new OrdType(OrdType.LIMIT));
        message.Set(new OrderQty(order.Quantity));
        message.Set(new Price(order.Price));
        return message;
    }

    private static OrderResult ToResult(string clOrdId, Message report)
    {
        var orderId = report.GetString(Tags.OrderID);
        var execId = report.GetString(Tags.ExecID);

        return report.GetChar(Tags.ExecType) switch
        {
            ExecType.NEW => new OrderResult(OrderOutcome.Accepted, clOrdId, orderId, execId),
            ExecType.REJECTED => new OrderResult(OrderOutcome.Rejected, clOrdId, orderId, execId,
                report.IsSetField(Tags.Text) ? report.GetString(Tags.Text) : null),
            _ => new OrderResult(OrderOutcome.UnexpectedReport, clOrdId, orderId, execId)
        };
    }

    public void FromApp(Message message, SessionID sessionID)
    {
        if (message.Header.GetString(Tags.MsgType) != MsgType.EXECUTION_REPORT || !message.IsSetField(Tags.ClOrdID))
            return;

        // Resposta que chega depois dos 5 s não acha mais quem esperava e é descartada.
        if (_pending.TryGetValue(message.GetString(Tags.ClOrdID), out var waiter))
            waiter.TrySetResult(message);
    }

    public void OnCreate(SessionID sessionID) => _sessionId = sessionID;

    public void OnLogon(SessionID sessionID) { }

    public void OnLogout(SessionID sessionID) { }

    public void ToAdmin(Message message, SessionID sessionID) { }

    public void FromAdmin(Message message, SessionID sessionID) { }

    public void ToApp(Message message, SessionID sessionID) { }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _initiator.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _initiator.Stop();
        return Task.CompletedTask;
    }

    public void Dispose() => _initiator.Dispose();
}
