using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using QuickFix;
using QuickFix.Fields;
using QuickFix.Logger;
using QuickFix.Store;

// Os testes sobem sessões FIX com o mesmo SessionID; o QuickFIX guarda as sessões num registro
// estático, então duas classes rodando juntas brigariam pelo mesmo nome.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace OrderGenerator.Tests;

// Faz o papel do OrderAccumulator só no que o OrderGenerator enxerga: aceita a sessão FIX,
// guarda as NewOrderSingle recebidas e responde com o ExecutionReport que o teste escolher.
public sealed class TestAcceptor : IApplication, IDisposable
{
    private ThreadedSocketAcceptor? _acceptor;
    private TaskCompletionSource _logon = NewSignal();
    private int _sequence;

    public TestAcceptor(int port) => Port = port;

    public int Port { get; }

    public ConcurrentQueue<Message> ReceivedOrders { get; } = new();

    // Devolve a resposta para a ordem recebida; null deixa a ordem sem resposta (acceptor mudo).
    public Func<Message, Message?> Responder { get; set; } = order => null;

    public void Start()
    {
        _logon = NewSignal();
        var settings = new SessionSettings(new StringReader($"""
            [DEFAULT]
            ConnectionType=acceptor
            SocketAcceptPort={Port}
            StartTime=00:00:00
            EndTime=00:00:00
            UseDataDictionary=Y
            DataDictionary={Path.Combine(AppContext.BaseDirectory, "FIX44.xml")}
            ResetOnLogon=Y
            ResetOnLogout=Y
            ResetOnDisconnect=Y

            [SESSION]
            BeginString=FIX.4.4
            SenderCompID=ORDERACCUMULATOR
            TargetCompID=ORDERGENERATOR
            """));
        _acceptor = new ThreadedSocketAcceptor(this, new MemoryStoreFactory(), settings, new NullLogFactory(), new DefaultMessageFactory());
        _acceptor.Start();
    }

    public void Stop()
    {
        _acceptor?.Stop(true);
        _acceptor?.Dispose();
        _acceptor = null;
    }

    // Espera as duas pontas: o acceptor recebeu o Logon e o initiator já recebeu a resposta dele.
    public async Task WaitForLogonAsync()
    {
        await _logon.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var initiator = new SessionID("FIX.4.4", "ORDERGENERATOR", "ORDERACCUMULATOR");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (Session.LookupSession(initiator)?.IsLoggedOn != true)
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(15))
                throw new TimeoutException("O initiator não logou.");
            await Task.Delay(50);
        }
    }

    public Message Accept(Message order)
    {
        var id = Interlocked.Increment(ref _sequence);
        var report = new QuickFix.FIX44.ExecutionReport(
            new OrderID($"ORD-{id}"), new ExecID($"EXE-{id}"),
            new ExecType(ExecType.NEW), new OrdStatus(OrdStatus.NEW),
            new Symbol(order.GetString(Tags.Symbol)), new Side(order.GetChar(Tags.Side)),
            new LeavesQty(order.GetDecimal(Tags.OrderQty)), new CumQty(0), new AvgPx(0));
        report.Set(new ClOrdID(order.GetString(Tags.ClOrdID)));
        return report;
    }

    public Message Reject(Message order, string text)
    {
        var id = Interlocked.Increment(ref _sequence);
        var report = new QuickFix.FIX44.ExecutionReport(
            new OrderID($"ORD-{id}"), new ExecID($"EXE-{id}"),
            new ExecType(ExecType.REJECTED), new OrdStatus(OrdStatus.REJECTED),
            new Symbol(order.GetString(Tags.Symbol)), new Side(order.GetChar(Tags.Side)),
            new LeavesQty(0), new CumQty(0), new AvgPx(0));
        report.Set(new ClOrdID(order.GetString(Tags.ClOrdID)));
        report.Set(new Text(text));
        return report;
    }

    public void FromApp(Message message, SessionID sessionID)
    {
        if (message.Header.GetString(Tags.MsgType) != MsgType.NEW_ORDER_D)
            return;

        ReceivedOrders.Enqueue(message);
        var reply = Responder(message);
        if (reply is not null)
            Session.SendToTarget(reply, sessionID);
    }

    public void OnLogon(SessionID sessionID) => _logon.TrySetResult();

    public void OnCreate(SessionID sessionID) { }

    public void OnLogout(SessionID sessionID) { }

    public void ToAdmin(Message message, SessionID sessionID) { }

    public void FromAdmin(Message message, SessionID sessionID) { }

    public void ToApp(Message message, SessionID sessionID) { }

    public void Dispose() => Stop();

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public static class TestHost
{
    // Porta livre na hora do teste; ninguém escuta nela até alguém subir algo.
    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static WebApplicationFactory<Program> Generator(int fixPort, string accumulatorUrl = "http://127.0.0.1:1", string? webRoot = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Fix:AcceptorHost", "127.0.0.1");
            builder.UseSetting("Fix:AcceptorPort", fixPort.ToString());
            builder.UseSetting("OrderAccumulator:BaseUrl", accumulatorUrl);
            if (webRoot is not null)
                builder.UseSetting(WebHostDefaults.WebRootKey, webRoot);
        });
}
