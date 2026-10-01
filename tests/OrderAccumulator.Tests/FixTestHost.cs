using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using OrderSideCodes = Flowa.Shared.OrderSideCodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QuickFix;
using QuickFix.Fields;
using QuickFix.FIX44;
using QuickFix.Store;
using QuickFix.Transport;
using Message = QuickFix.Message;

namespace OrderAccumulator.Tests;

// O OrderAccumulator inteiro (Program.cs), com o acceptor FIX em 127.0.0.1 numa porta livre
// (ou na porta pedida) e o banco do container.
public sealed class AccumulatorApp : WebApplicationFactory<Program>
{
    public const string LoopbackBindHost = "127.0.0.1";

    private readonly string connectionString;
    private readonly Action<IServiceCollection>? replaceServices;

    public AccumulatorApp(string connectionString, int? fixPort = null, Action<IServiceCollection>? replaceServices = null)
    {
        this.connectionString = connectionString;
        this.replaceServices = replaceServices;
        FixPort = fixPort ?? FindFreeTcpPort();
    }

    public int FixPort { get; }

    public CapturedLogs Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder webHostBuilder)
    {
        webHostBuilder.UseSetting("ConnectionStrings:Flowa", connectionString);
        webHostBuilder.UseSetting("Fix:AcceptorPort", FixPort.ToString());
        webHostBuilder.UseSetting("Fix:AcceptorBindHost", LoopbackBindHost);
        webHostBuilder.ConfigureLogging(loggingBuilder => loggingBuilder.AddProvider(Logs));
        if (replaceServices is not null)
            webHostBuilder.ConfigureTestServices(replaceServices);
    }

    // Força a subida do host (e do acceptor) sem precisar de uma chamada HTTP antes.
    public AccumulatorApp StartWithFixAcceptor()
    {
        _ = Services;
        return this;
    }

    private static int FindFreeTcpPort()
    {
        var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var freePort = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();
        return freePort;
    }
}

// Guarda o que o app escreveu no log, para conferir o log FIX (D-34).
public sealed class CapturedLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<string> capturedLines = new();

    public IReadOnlyList<string> Lines => capturedLines.ToList();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, capturedLines);

    public void Dispose() { }

    private sealed class CapturingLogger(string categoryName, ConcurrentQueue<string> capturedLines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            capturedLines.Enqueue($"{logLevel} {categoryName}: {formatter(state, exception)}");
    }
}

// A ponta initiator usada só nos testes: faz o papel do OrderGenerator sem depender dele.
public sealed class TestInitiator : IApplication, IDisposable
{
    private static readonly TimeSpan FixAnswerTimeout = TimeSpan.FromSeconds(10);

    private readonly SocketInitiator initiator;
    private readonly TaskCompletionSource loggedOn = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<ExecutionReport> executionReports = Channel.CreateUnbounded<ExecutionReport>();
    private readonly Channel<BusinessMessageReject> businessRejects = Channel.CreateUnbounded<BusinessMessageReject>();
    private SessionID? sessionId;

    private TestInitiator(int port)
    {
        var initiatorSettings = new SessionSettings(new StringReader($"""
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
        initiator = new SocketInitiator(this, new MemoryStoreFactory(), initiatorSettings, (ILoggerFactory?)null, null);
    }

    public static async Task<TestInitiator> LogOnToAcceptorAsync(int port)
    {
        var testInitiator = new TestInitiator(port);
        testInitiator.initiator.Start();
        await testInitiator.loggedOn.Task.WaitAsync(FixAnswerTimeout);
        return testInitiator;
    }

    public static NewOrderSingle NewOrder(string clOrdId, string symbol, char side, decimal quantity, decimal price) =>
        new(new ClOrdID(clOrdId), new Symbol(symbol), new Side(side), new TransactTime(DateTime.UtcNow), new OrdType(OrdType.LIMIT))
        {
            OrderQty = new OrderQty(quantity),
            Price = new Price(price)
        };

    public static NewOrderSingle NewBuyOrder(string symbol, decimal quantity, decimal price) =>
        NewOrder(Guid.NewGuid().ToString("N"), symbol, OrderSideCodes.BuyOrderSideFixCode, quantity, price);

    // Manda a ordem e devolve o ExecutionReport que voltou para ela.
    public async Task<ExecutionReport> SendExpectingExecutionReportAsync(NewOrderSingle order)
    {
        Assert.True(Session.SendToTarget(order, sessionId!));
        var executionReport = await executionReports.Reader.ReadAsync().AsTask().WaitAsync(FixAnswerTimeout);
        Assert.Equal(order.ClOrdID.Value, executionReport.ClOrdID.Value);
        return executionReport;
    }

    // Manda a ordem e confere que nenhum ExecutionReport volta dentro do prazo.
    public async Task ExpectNoAnswerAsync(NewOrderSingle order, TimeSpan noAnswerWindow)
    {
        Assert.True(Session.SendToTarget(order, sessionId!));
        // Leitura cancelável: um ReadAsync pendurado depois do prazo engoliria o próximo relatório.
        using var timeout = new CancellationTokenSource(noAnswerWindow);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executionReports.Reader.ReadAsync(timeout.Token).AsTask());
    }

    // Manda uma mensagem que a aplicação do acceptor não aceita e devolve a recusa (35=j) que voltou.
    public async Task<BusinessMessageReject> SendExpectingBusinessRejectAsync(NewOrderSingle order)
    {
        Assert.True(Session.SendToTarget(order, sessionId!));
        return await businessRejects.Reader.ReadAsync().AsTask().WaitAsync(FixAnswerTimeout);
    }

    public void Dispose() => initiator.Dispose();

    public void OnLogon(SessionID sessionId)
    {
        this.sessionId = sessionId;
        loggedOn.TrySetResult();
    }

    public void FromApp(Message message, SessionID sessionId)
    {
        if (message is ExecutionReport executionReport)
            executionReports.Writer.TryWrite(executionReport);
        else if (message is BusinessMessageReject businessReject)
            businessRejects.Writer.TryWrite(businessReject);
    }

    public void OnCreate(SessionID sessionId) { }
    public void OnLogout(SessionID sessionId) { }
    public void ToAdmin(Message message, SessionID sessionId) { }
    public void FromAdmin(Message message, SessionID sessionId) { }
    public void ToApp(Message message, SessionID sessionId) { }
}
