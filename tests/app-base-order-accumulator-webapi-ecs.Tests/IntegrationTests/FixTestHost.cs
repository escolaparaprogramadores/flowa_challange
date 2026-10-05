using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Net;
using System.Threading.Channels;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QuickFix.Fields;
using QuickFix.FIX44;
using QuickFix.Store;
using QuickFix.Transport;
using QuickFix;
using OrderSideCodes = Base.OrderAccumulator.Domain.Orders.OrderSideCodes;
using Message = QuickFix.Message;

namespace Base.OrderAccumulator.Tests;

// O OrderAccumulator inteiro (Program.cs), com o acceptor FIX em 127.0.0.1 numa porta livre
// (ou na porta pedida) e o banco do container.
public sealed class OrderAccumulatorFixTestHost : WebApplicationFactory<Program>
{
    public const string FixAcceptorLoopbackBindHost = "127.0.0.1";

    private readonly string orderDatabaseConnectionString;
    private readonly Action<IServiceCollection>? replaceOrderAccumulatorServices;

    public OrderAccumulatorFixTestHost(string orderDatabaseConnectionString, int? fixAcceptorPort = null, Action<IServiceCollection>? replaceOrderAccumulatorServices = null)
    {
        this.orderDatabaseConnectionString = orderDatabaseConnectionString;
        this.replaceOrderAccumulatorServices = replaceOrderAccumulatorServices;
        FixAcceptorPort = fixAcceptorPort ?? FindFreeFixAcceptorTcpPort();
    }

    public int FixAcceptorPort { get; }

    public OrderAccumulatorCapturedLogs CapturedOrderAccumulatorLogs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder orderAccumulatorWebHostBuilder)
    {
        orderAccumulatorWebHostBuilder.UseSetting("ConnectionStrings:Flowa", orderDatabaseConnectionString);
        orderAccumulatorWebHostBuilder.UseSetting("Fix:AcceptorPort", FixAcceptorPort.ToString());
        orderAccumulatorWebHostBuilder.UseSetting("Fix:AcceptorBindHost", FixAcceptorLoopbackBindHost);
        orderAccumulatorWebHostBuilder.ConfigureLogging(orderAccumulatorLoggingBuilder => orderAccumulatorLoggingBuilder.AddProvider(CapturedOrderAccumulatorLogs));
        if (replaceOrderAccumulatorServices is not null)
            orderAccumulatorWebHostBuilder.ConfigureTestServices(replaceOrderAccumulatorServices);
    }

    // Força a subida do host (e do acceptor) sem precisar de uma chamada HTTP antes.
    public OrderAccumulatorFixTestHost StartWithFixAcceptor()
    {
        _ = Services;
        return this;
    }

    public static int FindFreeFixAcceptorTcpPort()
    {
        var fixAcceptorPortProbe = new TcpListener(IPAddress.Loopback, 0);
        fixAcceptorPortProbe.Start();
        var freeFixAcceptorPort = ((IPEndPoint)fixAcceptorPortProbe.LocalEndpoint).Port;
        fixAcceptorPortProbe.Stop();
        return freeFixAcceptorPort;
    }
}

// Guarda o que o app escreveu no log, para conferir o log FIX (D-34).
public sealed class OrderAccumulatorCapturedLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<string> capturedOrderAccumulatorLogLines = new();
    public IReadOnlyList<string> CapturedLogLines => capturedOrderAccumulatorLogLines.ToList();
    public ILogger CreateLogger(string categoryName) => new OrderAccumulatorLogCaptureLogger(categoryName, capturedOrderAccumulatorLogLines);
    public void Dispose() { }

    private sealed class OrderAccumulatorLogCaptureLogger(string categoryName, ConcurrentQueue<string> capturedOrderAccumulatorLogLines) : ILogger
    {
        public IDisposable? BeginScope<TLogScopeState>(TLogScopeState logScopeState) where TLogScopeState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TLogEntryState>(LogLevel logLevel, EventId logEventId, TLogEntryState logEntryState, Exception? loggedException,
            Func<TLogEntryState, Exception?, string> logMessageFormatter) =>
            capturedOrderAccumulatorLogLines.Enqueue($"{logLevel} {categoryName}: {logMessageFormatter(logEntryState, loggedException)}");
    }
}

// A ponta initiator usada só nos testes: faz o papel do OrderGenerator sem depender dele.
public sealed class FixTestInitiator : IApplication, IDisposable
{
    private static readonly TimeSpan FixAnswerTimeout = TimeSpan.FromSeconds(10);

    private readonly SocketInitiator fixTestSocketInitiator;
    private readonly TaskCompletionSource fixSessionLoggedOn = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<ExecutionReport> executionReports = Channel.CreateUnbounded<ExecutionReport>();
    private readonly Channel<BusinessMessageReject> businessMessageRejects = Channel.CreateUnbounded<BusinessMessageReject>();
    private readonly Channel<Reject> sessionRejects = Channel.CreateUnbounded<Reject>();
    private SessionID? fixSessionId;
    private int receivedHeartbeatCount;

    private FixTestInitiator(int fixAcceptorPort, int heartbeatIntervalSeconds)
    {
        var fixTestInitiatorSettings = new SessionSettings(new StringReader($"""
            [DEFAULT]
            ConnectionType=initiator
            ReconnectInterval=1
            HeartBtInt={heartbeatIntervalSeconds}
            StartTime=00:00:00
            EndTime=00:00:00
            UseDataDictionary=Y
            DataDictionary={Path.Combine(AppContext.BaseDirectory, "FIX44-flowa.xml")}
            ResetOnLogon=Y
            ResetOnLogout=Y
            ResetOnDisconnect=Y

            [SESSION]
            BeginString=FIX.4.4
            SenderCompID=ORDERGENERATOR
            TargetCompID=ORDERACCUMULATOR
            SocketConnectHost=127.0.0.1
            SocketConnectPort={fixAcceptorPort}
            """));
        fixTestSocketInitiator = new SocketInitiator(this, new MemoryStoreFactory(), fixTestInitiatorSettings, (ILoggerFactory?)null, null);
    }

    // Heartbeats (35=0) the acceptor sent to this initiator: the acceptor sends one whenever the session is quiet
    // for the interval the initiator asked for at logon.
    public int ReceivedHeartbeatCount => Volatile.Read(ref receivedHeartbeatCount);

    public static async Task<FixTestInitiator> LogOnToAcceptorAsync(int fixAcceptorPort, int heartbeatIntervalSeconds = 30)
    {
        var fixTestInitiator = new FixTestInitiator(fixAcceptorPort, heartbeatIntervalSeconds);
        fixTestInitiator.fixTestSocketInitiator.Start();
        await fixTestInitiator.fixSessionLoggedOn.Task.WaitAsync(FixAnswerTimeout);
        return fixTestInitiator;
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
    public async Task<ExecutionReport> SendExpectingExecutionReportAsync(NewOrderSingle newOrderSingle)
    {
        Assert.True(Session.SendToTarget(newOrderSingle, fixSessionId!));
        var receivedExecutionReport = await executionReports.Reader.ReadAsync().AsTask().WaitAsync(FixAnswerTimeout);
        Assert.Equal(newOrderSingle.ClOrdID.Value, receivedExecutionReport.ClOrdID.Value);
        return receivedExecutionReport;
    }

    // Manda a ordem e confere que nada volta dentro do prazo: nem ExecutionReport, nem BusinessMessageReject.
    public async Task ExpectNoAnswerAsync(NewOrderSingle newOrderSingle, TimeSpan noAnswerWindow)
    {
        Assert.True(Session.SendToTarget(newOrderSingle, fixSessionId!));
        // Leitura cancelável: um ReadAsync pendurado depois do prazo engoliria o próximo relatório.
        using var noAnswerTimeout = new CancellationTokenSource(noAnswerWindow);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executionReports.Reader.ReadAsync(noAnswerTimeout.Token).AsTask());
        Assert.False(businessMessageRejects.Reader.TryRead(out _), "Veio um BusinessMessageReject onde não devia vir resposta nenhuma.");
    }

    // Manda uma mensagem que a aplicação do acceptor não aceita e devolve a recusa (35=j) que voltou.
    public async Task<BusinessMessageReject> SendExpectingBusinessRejectAsync(NewOrderSingle newOrderSingle)
    {
        Assert.True(Session.SendToTarget(newOrderSingle, fixSessionId!));
        return await businessMessageRejects.Reader.ReadAsync().AsTask().WaitAsync(FixAnswerTimeout);
    }

    // Manda uma mensagem que a validação da sessão FIX barra e devolve a recusa de sessão (35=3).
    public async Task<Reject> SendExpectingSessionRejectAsync(NewOrderSingle newOrderSingle)
    {
        Assert.True(Session.SendToTarget(newOrderSingle, fixSessionId!));
        return await sessionRejects.Reader.ReadAsync().AsTask().WaitAsync(FixAnswerTimeout);
    }

    public void Dispose() => fixTestSocketInitiator.Dispose();

    public void OnLogon(SessionID fixSessionId)
    {
        this.fixSessionId = fixSessionId;
        fixSessionLoggedOn.TrySetResult();
    }

    public void FromApp(Message fixMessage, SessionID fixSessionId)
    {
        if (fixMessage is ExecutionReport receivedExecutionReport)
            executionReports.Writer.TryWrite(receivedExecutionReport);
        else if (fixMessage is BusinessMessageReject receivedBusinessMessageReject)
            businessMessageRejects.Writer.TryWrite(receivedBusinessMessageReject);
    }

    public void OnCreate(SessionID fixSessionId) { }
    public void OnLogout(SessionID fixSessionId) { }
    public void ToAdmin(Message fixMessage, SessionID fixSessionId) { }
    public void FromAdmin(Message fixMessage, SessionID fixSessionId)
    {
        if (fixMessage is Reject receivedSessionReject)
            sessionRejects.Writer.TryWrite(receivedSessionReject);
        else if (fixMessage is Heartbeat)
            Interlocked.Increment(ref receivedHeartbeatCount);
    }
    public void ToApp(Message fixMessage, SessionID fixSessionId) { }
}
