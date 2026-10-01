using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using SideCodes = Flowa.Shared.SideCodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using QuickFix;
using QuickFix.Fields;
using QuickFix.FIX44;
using QuickFix.Store;
using QuickFix.Transport;
using Message = QuickFix.Message;

namespace OrderAccumulator.Tests;

// O OrderAccumulator inteiro (Program.cs), com o acceptor FIX numa porta livre e o banco do container.
public sealed class AccumulatorApp : WebApplicationFactory<Program>
{
    private readonly string connectionString;

    public AccumulatorApp(string connectionString)
    {
        this.connectionString = connectionString;
        FixPort = FreeTcpPort();
    }

    public int FixPort { get; }

    public CapturedLogs Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Flowa", connectionString);
        builder.UseSetting("Fix:AcceptorPort", FixPort.ToString());
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
    }

    // Força a subida do host (e do acceptor) sem precisar de uma chamada HTTP antes.
    public AccumulatorApp Start()
    {
        _ = Services;
        return this;
    }

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

// Guarda o que o app escreveu no log, para conferir o log FIX (D-34).
public sealed class CapturedLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<string> lines = new();

    public IReadOnlyList<string> Lines => lines.ToList();

    public ILogger CreateLogger(string categoryName) => new Collector(categoryName, lines);

    public void Dispose() { }

    private sealed class Collector(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)}");
    }
}

// A ponta initiator usada só nos testes: faz o papel do OrderGenerator sem depender dele.
public sealed class TestInitiator : IApplication, IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly SocketInitiator initiator;
    private readonly TaskCompletionSource loggedOn = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<ExecutionReport> reports = Channel.CreateUnbounded<ExecutionReport>();
    private SessionID? session;

    private TestInitiator(int port)
    {
        var settings = new SessionSettings(new StringReader($"""
            [DEFAULT]
            ConnectionType=initiator
            ReconnectInterval=1
            HeartBtInt=30
            StartTime=00:00:00
            EndTime=00:00:00
            UseDataDictionary=Y
            DataDictionary={Path.Combine(AppContext.BaseDirectory, "FIX44.xml")}
            ResetOnLogon=Y
            ResetOnLogout=Y
            ResetOnDisconnect=Y

            [SESSION]
            BeginString=FIX.4.4
            SenderCompID=ORDERGENERATOR
            TargetCompID=ORDERACCUMULATOR
            SocketConnectHost=127.0.0.1
            SocketConnectPort={port}
            """));
        initiator = new SocketInitiator(this, new MemoryStoreFactory(), settings, (ILoggerFactory?)null, null);
    }

    public static async Task<TestInitiator> ConnectAsync(int port)
    {
        var testInitiator = new TestInitiator(port);
        testInitiator.initiator.Start();
        await testInitiator.loggedOn.Task.WaitAsync(Wait);
        return testInitiator;
    }

    public static NewOrderSingle Order(string clOrdId, string symbol, char side, decimal quantity, decimal price) =>
        new(new ClOrdID(clOrdId), new Symbol(symbol), new Side(side), new TransactTime(DateTime.UtcNow), new OrdType(OrdType.LIMIT))
        {
            OrderQty = new OrderQty(quantity),
            Price = new Price(price)
        };

    public static NewOrderSingle Buy(string symbol, decimal quantity, decimal price) =>
        Order(Guid.NewGuid().ToString("N"), symbol, SideCodes.BuyFix, quantity, price);

    // Manda a ordem e devolve o ExecutionReport que voltou para ela.
    public async Task<ExecutionReport> SendAsync(NewOrderSingle order)
    {
        Assert.True(Session.SendToTarget(order, session!));
        var report = await reports.Reader.ReadAsync().AsTask().WaitAsync(Wait);
        Assert.Equal(order.ClOrdID.Value, report.ClOrdID.Value);
        return report;
    }

    public void Dispose() => initiator.Dispose();

    public void OnLogon(SessionID sessionId)
    {
        session = sessionId;
        loggedOn.TrySetResult();
    }

    public void FromApp(Message message, SessionID sessionId)
    {
        if (message is ExecutionReport report)
            reports.Writer.TryWrite(report);
    }

    public void OnCreate(SessionID sessionId) { }
    public void OnLogout(SessionID sessionId) { }
    public void ToAdmin(Message message, SessionID sessionId) { }
    public void FromAdmin(Message message, SessionID sessionId) { }
    public void ToApp(Message message, SessionID sessionId) { }
}
