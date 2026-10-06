using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Net;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuickFix.Fields;
using QuickFix.FIX44;
using QuickFix.Store;
using QuickFix.Transport;
using QuickFix;
using OrderSideCodes = Flowa.OrderAccumulator.Domain.Orders.Enums.OrderSideCodes;
using Message = QuickFix.Message;

namespace Flowa.OrderAccumulator.Tests;

// The whole OrderAccumulator, composed and started by the same Program methods the worker runs, with the FIX acceptor
// on 127.0.0.1 on a free port (or on the requested port) and the container database. The worker has no HTTP.
public sealed class OrderAccumulatorFixTestHost : IAsyncDisposable
{
    public const string FixAcceptorLoopbackBindHost = "127.0.0.1";

    private readonly string orderDatabaseConnectionString;
    private readonly Action<IServiceCollection>? replaceOrderAccumulatorServices;
    private readonly IReadOnlyDictionary<string, string?> extraOrderAccumulatorSettings;
    private IHost? orderAccumulatorHost;

    public OrderAccumulatorFixTestHost(
        string orderDatabaseConnectionString, int? fixAcceptorPort = null, Action<IServiceCollection>? replaceOrderAccumulatorServices = null,
        IReadOnlyDictionary<string, string?>? extraOrderAccumulatorSettings = null)
    {
        this.orderDatabaseConnectionString = orderDatabaseConnectionString;
        this.replaceOrderAccumulatorServices = replaceOrderAccumulatorServices;
        this.extraOrderAccumulatorSettings = extraOrderAccumulatorSettings ?? new Dictionary<string, string?>();
        FixAcceptorPort = fixAcceptorPort ?? FindFreeFixAcceptorTcpPort();
    }

    public int FixAcceptorPort { get; }

    public OrderAccumulatorCapturedLogs CapturedOrderAccumulatorLogs { get; } = new();

    public IServiceProvider Services =>
        orderAccumulatorHost?.Services ?? throw new InvalidOperationException("Start the OrderAccumulator with StartWithFixAcceptorAsync first.");

    public async Task<OrderAccumulatorFixTestHost> StartWithFixAcceptorAsync()
    {
        var orderAccumulatorBuilder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development,
            ContentRootPath = AppContext.BaseDirectory
        });
        orderAccumulatorBuilder.Configuration.AddInMemoryCollection(BuildOrderAccumulatorTestSettings());
        Program.AddOrderAccumulatorServices(orderAccumulatorBuilder);
        orderAccumulatorBuilder.Logging.AddProvider(CapturedOrderAccumulatorLogs);
        replaceOrderAccumulatorServices?.Invoke(orderAccumulatorBuilder.Services);

        orderAccumulatorHost = orderAccumulatorBuilder.Build();
        await Program.StartOrderAccumulatorAsync(orderAccumulatorHost);
        return this;
    }

    public async ValueTask DisposeAsync()
    {
        if (orderAccumulatorHost is null)
            return;

        await orderAccumulatorHost.StopAsync();
        orderAccumulatorHost.Dispose();
    }

    private Dictionary<string, string?> BuildOrderAccumulatorTestSettings()
    {
        var orderAccumulatorTestSettings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Flowa"] = orderDatabaseConnectionString,
            ["Fix:AcceptorPort"] = FixAcceptorPort.ToString(),
            ["Fix:AcceptorBindHost"] = FixAcceptorLoopbackBindHost
        };
        foreach (var (extraSettingKey, extraSettingValue) in extraOrderAccumulatorSettings)
            orderAccumulatorTestSettings[extraSettingKey] = extraSettingValue;
        return orderAccumulatorTestSettings;
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

// Keeps what the app wrote to the log, to check the FIX log (D-34).
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

// The initiator end used only in the tests: plays the OrderGenerator without depending on it.
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

    // Sends the order and returns the ExecutionReport that came back for it.
    public async Task<ExecutionReport> SendExpectingExecutionReportAsync(NewOrderSingle newOrderSingle)
    {
        Assert.True(Session.SendToTarget(newOrderSingle, fixSessionId!));
        var receivedExecutionReport = await executionReports.Reader.ReadAsync().AsTask().WaitAsync(FixAnswerTimeout);
        Assert.Equal(newOrderSingle.ClOrdID.Value, receivedExecutionReport.ClOrdID.Value);
        return receivedExecutionReport;
    }

    public async Task<IReadOnlyDictionary<string, ExecutionReport>> SendAllAtOnceExpectingExecutionReportsAsync(
        IReadOnlyList<NewOrderSingle> simultaneousOrders, TimeSpan allAnswersTimeout)
    {
        var wasEachOrderSent = await Task.WhenAll(simultaneousOrders.Select(simultaneousOrder => Task.Run(() => Session.SendToTarget(simultaneousOrder, fixSessionId!))));
        Assert.All(wasEachOrderSent, Assert.True);

        using var allAnswersDeadline = new CancellationTokenSource(allAnswersTimeout);
        var executionReportsByClOrdId = new Dictionary<string, ExecutionReport>();
        while (executionReportsByClOrdId.Count < simultaneousOrders.Count)
        {
            var receivedExecutionReport = await executionReports.Reader.ReadAsync(allAnswersDeadline.Token);
            Assert.True(executionReportsByClOrdId.TryAdd(receivedExecutionReport.ClOrdID.Value, receivedExecutionReport),
                $"Two ExecutionReports came for the ClOrdID {receivedExecutionReport.ClOrdID.Value}.");
        }
        return executionReportsByClOrdId;
    }

    // Sends the order and checks that nothing comes back within the deadline: neither ExecutionReport nor BusinessMessageReject.
    public async Task ExpectNoAnswerAsync(NewOrderSingle newOrderSingle, TimeSpan noAnswerWindow)
    {
        Assert.True(Session.SendToTarget(newOrderSingle, fixSessionId!));
        // Cancellable read: a ReadAsync left hanging after the deadline would swallow the next report.
        using var noAnswerTimeout = new CancellationTokenSource(noAnswerWindow);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executionReports.Reader.ReadAsync(noAnswerTimeout.Token).AsTask());
        Assert.False(businessMessageRejects.Reader.TryRead(out _), "A BusinessMessageReject came where no answer at all should come.");
    }

    // Sends a message the acceptor application does not accept and returns the refusal (35=j) that came back.
    public async Task<BusinessMessageReject> SendExpectingBusinessRejectAsync(NewOrderSingle newOrderSingle)
    {
        Assert.True(Session.SendToTarget(newOrderSingle, fixSessionId!));
        return await businessMessageRejects.Reader.ReadAsync().AsTask().WaitAsync(FixAnswerTimeout);
    }

    // Sends a message the FIX session validation blocks and returns the session refusal (35=3).
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
