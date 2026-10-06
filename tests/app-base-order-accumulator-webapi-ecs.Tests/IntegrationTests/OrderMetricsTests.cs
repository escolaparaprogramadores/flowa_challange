using System.Net.Sockets;
using System.Net;
using System.Text;
using Base.OrderAccumulator.Application.Exposures.Interfaces;
using Base.OrderAccumulator.Application.Orders.Interfaces;
using Base.OrderAccumulator.Application.Orders.Responses;
using Base.OrderAccumulator.Commons.Database;
using Base.OrderAccumulator.Commons.Logging;
using Base.OrderAccumulator.Commons.Observability;
using Base.OrderAccumulator.Commons.Responses;
using Base.OrderAccumulator.Domain.Exposures.ValueObjects;
using Base.OrderAccumulator.Domain.Orders.Entities;
using Base.OrderAccumulator.Domain.Orders.Interfaces;
using Base.OrderAccumulator.Entrypoint.BackgroundService;
using Base.OrderAccumulator.Infrastructure.DependencyInjection;
using Base.OrderAccumulator.Infrastructure.Exposures.Adapters;
using Base.OrderAccumulator.Infrastructure.Orders.Adapters;
using Base.OrderAccumulator.Infrastructure.Orders.Repositories;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Base.OrderAccumulator.Tests;

// The metrics go out over real UDP to a local listener, in place of the Datadog agent,
// and the processing uses the real PostgreSQL: accepted, rejected and repeat come from the database.
[Collection(OrderAccumulatorPostgresCollection.Name)]
public sealed class OrderMetricsTests(OrderAccumulatorPostgresFixture orderAccumulatorDatabase) : IAsyncLifetime
{
    private const string SentinelMetricName = "flowa.test.sentinel";
    private static readonly string[] UnifiedServiceTags = ["env:dev", "service:order-accumulator", "version:test-sha"];

    private readonly DogStatsdUdpListener dogStatsdUdpListener = new();
    private DogStatsdMetricsClient orderMetricsClient = null!;
    private ISymbolExposureMemoryPort symbolExposureMemory = null!;
    private DecideIncomingOrderTestRunner meteredOrderDecisionRunner = null!;

    public async Task InitializeAsync()
    {
        await orderAccumulatorDatabase.ResetOrdersAndExposuresAsync();
        orderMetricsClient = OrderMetricsExtensions.CreateOrderMetricsClient(dogStatsdUdpListener.ListenerPort, UnifiedServiceConfiguration());
        symbolExposureMemory = new InMemorySymbolExposureAdapter();
        symbolExposureMemory.LoadStoredExposures(await orderAccumulatorDatabase.ExposureReader.GetSymbolExposuresAsync());
        meteredOrderDecisionRunner = new DecideIncomingOrderTestRunner(
            orderAccumulatorDatabase.OrderDatabaseConnectionSource, symbolExposureMemory, new DatadogOrderMetricsAdapter(orderMetricsClient));
    }

    public Task DisposeAsync()
    {
        orderMetricsClient.Dispose();
        dogStatsdUdpListener.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Accepted_order_sends_one_accepted_count_with_symbol_side_and_unified_tags()
    {
        var acceptedOrderDecision = await meteredOrderDecisionRunner.DecideIncomingOrderAsync(TestOrders.NewBuyOrder("PETR4", 100, 10.50m));

        var sentOrderMetrics = await SendSentinelAndReadOrderMetricsAsync();

        Assert.True(acceptedOrderDecision.Accepted);
        var acceptedOrderMetric = Assert.Single(sentOrderMetrics);
        Assert.Equal(new DogStatsdMetricLine(OrderMetricNames.AcceptedOrders, "1", "c", ExpectedTags("symbol:PETR4", "side:buy")), acceptedOrderMetric);
    }

    [Fact]
    public async Task Order_over_the_exposure_limit_sends_one_rejected_count_with_symbol_and_side()
    {
        await SetStoredExposureAsync("VALE3", -ExposureLimitPolicy.PerSymbol);
        var rejectedOrderDecision = await meteredOrderDecisionRunner.DecideIncomingOrderAsync(TestOrders.NewSellOrder("VALE3", 1, 0.01m));

        var sentOrderMetrics = await SendSentinelAndReadOrderMetricsAsync();

        Assert.False(rejectedOrderDecision.Accepted);
        var rejectedOrderMetric = Assert.Single(sentOrderMetrics);
        Assert.Equal(new DogStatsdMetricLine(OrderMetricNames.RejectedOrders, "1", "c", ExpectedTags("symbol:VALE3", "side:sell")), rejectedOrderMetric);
        Assert.Equal(-ExposureLimitPolicy.PerSymbol, ExposureInMemory("VALE3"));
    }

    [Fact]
    public async Task Repeated_clordid_is_counted_once_and_moves_the_exposure_memory_once()
    {
        var firstBuyOrder = TestOrders.NewBuyOrder("VIIA4", 200, 3.25m);
        await meteredOrderDecisionRunner.DecideIncomingOrderAsync(firstBuyOrder);
        var repeatedOrderDecision = await meteredOrderDecisionRunner.DecideIncomingOrderAsync(firstBuyOrder);

        var sentOrderMetrics = await SendSentinelAndReadOrderMetricsAsync();

        Assert.True(repeatedOrderDecision.IsRepeat);
        var acceptedOrderMetric = Assert.Single(sentOrderMetrics);
        Assert.Equal(new DogStatsdMetricLine(OrderMetricNames.AcceptedOrders, "1", "c", ExpectedTags("symbol:VIIA4", "side:buy")), acceptedOrderMetric);
        Assert.Equal(650m, ExposureInMemory("VIIA4"));
        Assert.Equal(650m, await orderAccumulatorDatabase.ReadExposureOfSymbolAsync("VIIA4"));
    }

    [Fact]
    public async Task Unknown_symbol_is_counted_as_invalid_symbol_and_side_never_as_the_typed_text()
    {
        await meteredOrderDecisionRunner.DecideIncomingOrderAsync(TestOrders.NewBuyOrder("ABCD3-texto-livre", 10, 1m));

        var sentOrderMetrics = await SendSentinelAndReadOrderMetricsAsync();

        var rejectedOrderMetric = Assert.Single(sentOrderMetrics);
        Assert.Equal(new DogStatsdMetricLine(OrderMetricNames.RejectedOrders, "1", "c", ExpectedTags("symbol:invalido", "side:invalido")), rejectedOrderMetric);
        Assert.Equal(
            [new SymbolExposure("PETR4", 0m), new SymbolExposure("VALE3", 0m), new SymbolExposure("VIIA4", 0m)],
            symbolExposureMemory.ReadCurrentSymbolExposures());
    }

    [Fact]
    public async Task Unknown_side_with_a_known_symbol_is_counted_as_invalid_side()
    {
        await meteredOrderDecisionRunner.DecideIncomingOrderAsync(TestOrders.NewIncomingOrder("PETR4", '3', 10, 1m));

        var sentOrderMetrics = await SendSentinelAndReadOrderMetricsAsync();

        var rejectedOrderMetric = Assert.Single(sentOrderMetrics);
        Assert.Equal(new DogStatsdMetricLine(OrderMetricNames.RejectedOrders, "1", "c", ExpectedTags("symbol:PETR4", "side:invalido")), rejectedOrderMetric);
        Assert.Equal(0m, ExposureInMemory("PETR4"));
    }

    [Fact]
    public async Task Order_tags_never_carry_the_clordid()
    {
        var acceptedSellOrder = TestOrders.NewSellOrder("PETR4", 5, 2m);
        await meteredOrderDecisionRunner.DecideIncomingOrderAsync(acceptedSellOrder);

        var sentOrderMetrics = await SendSentinelAndReadOrderMetricsAsync();

        var acceptedOrderMetric = Assert.Single(sentOrderMetrics);
        Assert.Equal(ExpectedTags("symbol:PETR4", "side:sell"), acceptedOrderMetric.MetricTags);
        Assert.DoesNotContain(acceptedOrderMetric.MetricTags, metricTag => metricTag.Contains(acceptedSellOrder.ClOrdId));
    }

    [Fact]
    public async Task Failed_processing_is_not_counted_and_the_same_exception_reaches_the_caller()
    {
        var databaseFailure = new NpgsqlException("database down");
        var meteredRunnerWithFailingDatabase = new DecideIncomingOrderTestRunner(
            orderAccumulatorDatabase.OrderDatabaseConnectionSource, symbolExposureMemory, new DatadogOrderMetricsAdapter(orderMetricsClient),
            wrapOrderRepository: _ => new OrderRepositoryFailingWith(databaseFailure));

        var failedOrderDecisionMessage = await meteredRunnerWithFailingDatabase.DecideIncomingOrderMessageAsync(TestOrders.NewBuyOrder("PETR4", 1, 1m));
        var sentOrderMetrics = await SendSentinelAndReadOrderMetricsAsync();

        Assert.False(failedOrderDecisionMessage.Success);
        Assert.Equal(ResultStatus.InternalError, failedOrderDecisionMessage.Status);
        Assert.Equal("internal-error", failedOrderDecisionMessage.ErrorCode);
        Assert.Same(databaseFailure, failedOrderDecisionMessage.UnexpectedFailure);
        Assert.Empty(sentOrderMetrics);
        Assert.Equal(0m, ExposureInMemory("PETR4"));
    }

    [Fact]
    public async Task Exposure_gauge_sends_the_three_symbols_from_memory_after_accepted_buys_and_sells()
    {
        await meteredOrderDecisionRunner.DecideIncomingOrderAsync(TestOrders.NewBuyOrder("PETR4", 1000, 12.34m));
        await meteredOrderDecisionRunner.DecideIncomingOrderAsync(TestOrders.NewSellOrder("VALE3", 300, 50.10m));
        await SendSentinelAndReadOrderMetricsAsync();

        new SymbolExposureGaugeBackgroundService(new DatadogOrderMetricsAdapter(orderMetricsClient), symbolExposureMemory, TimeProvider.System,
            TestObservability.CreateDiscardingLogger<SymbolExposureGaugeBackgroundService>()).SendSymbolExposureGauges();
        var sentExposureGauges = await SendSentinelAndReadOrderMetricsAsync();

        Assert.Equal(
            [
                new DogStatsdMetricLine(OrderMetricNames.SymbolExposure, "12340", "g", ExpectedTags("symbol:PETR4")),
                new DogStatsdMetricLine(OrderMetricNames.SymbolExposure, "-15030", "g", ExpectedTags("symbol:VALE3")),
                new DogStatsdMetricLine(OrderMetricNames.SymbolExposure, "0", "g", ExpectedTags("symbol:VIIA4"))
            ],
            sentExposureGauges.OrderBy(exposureGauge => exposureGauge.MetricTags.Single(metricTag => metricTag.StartsWith("symbol:"))));
    }

    [Fact]
    public async Task Exposure_gauge_goes_at_startup_and_again_on_each_30_second_tick()
    {
        await meteredOrderDecisionRunner.DecideIncomingOrderAsync(TestOrders.NewBuyOrder("PETR4", 10, 2m));
        await SendSentinelAndReadOrderMetricsAsync();
        var manualGaugeClock = new ManualGaugeClock();
        using var symbolExposureGaugeService = new SymbolExposureGaugeBackgroundService(new DatadogOrderMetricsAdapter(orderMetricsClient), symbolExposureMemory, manualGaugeClock,
            TestObservability.CreateDiscardingLogger<SymbolExposureGaugeBackgroundService>());
        IReadOnlyList<DogStatsdMetricLine> expectedExposureGauges =
        [
            new DogStatsdMetricLine(OrderMetricNames.SymbolExposure, "20", "g", ExpectedTags("symbol:PETR4")),
            new DogStatsdMetricLine(OrderMetricNames.SymbolExposure, "0", "g", ExpectedTags("symbol:VALE3")),
            new DogStatsdMetricLine(OrderMetricNames.SymbolExposure, "0", "g", ExpectedTags("symbol:VIIA4"))
        ];

        await symbolExposureGaugeService.StartAsync(CancellationToken.None);
        var gaugesAtStartup = await ReadThreeExposureGaugesAsync();
        var gaugesBeforeTheTick = await SendSentinelAndReadOrderMetricsAsync();
        manualGaugeClock.TickGaugeTimer();
        var gaugesAfterTheTick = await ReadThreeExposureGaugesAsync();
        await symbolExposureGaugeService.StopAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(30), manualGaugeClock.GaugeTimerDueTime);
        Assert.Equal(TimeSpan.FromSeconds(30), manualGaugeClock.GaugeTimerPeriod);
        Assert.Equal(expectedExposureGauges, gaugesAtStartup);
        Assert.Empty(gaugesBeforeTheTick);
        Assert.Equal(expectedExposureGauges, gaugesAfterTheTick);
    }

    // CA-28: the gauge loop writes one Information line when it starts and nothing on its 30-second ticks.
    [Fact]
    public async Task Exposure_gauge_logs_only_when_its_loop_starts_and_nothing_on_two_ticks()
    {
        var manualGaugeClock = new ManualGaugeClock();
        var recordingGaugeLogger = new RecordingApplicationLogger<SymbolExposureGaugeBackgroundService>();
        using var symbolExposureGaugeService = new SymbolExposureGaugeBackgroundService(
            new DatadogOrderMetricsAdapter(orderMetricsClient), symbolExposureMemory, manualGaugeClock, recordingGaugeLogger);

        await symbolExposureGaugeService.StartAsync(CancellationToken.None);
        var gaugesAtStartup = await ReadThreeExposureGaugesAsync();
        manualGaugeClock.TickGaugeTimer();
        var gaugesAfterTheFirstTick = await ReadThreeExposureGaugesAsync();
        manualGaugeClock.TickGaugeTimer();
        var gaugesAfterTheSecondTick = await ReadThreeExposureGaugesAsync();
        await symbolExposureGaugeService.StopAsync(CancellationToken.None);

        Assert.Equal(3, gaugesAtStartup.Count);
        Assert.Equal(3, gaugesAfterTheFirstTick.Count);
        Assert.Equal(3, gaugesAfterTheSecondTick.Count);
        Assert.Equal(["Information Symbol exposure gauge loop started."], recordingGaugeLogger.RecordedLogLines);
    }

    [Fact]
    public async Task App_sends_order_metrics_to_the_agent_on_localhost_8125_with_the_dd_tags()
    {
        // The unique version separates the metrics of this test from those of another test app using the same port.
        var uniqueVersionTag = "sha-" + Guid.NewGuid().ToString("N");
        using var agentOnTheDatadogPort = new DogStatsdUdpListener(OrderMetricsExtensions.DatadogAgentDogStatsdPort);
        var startupDatabaseConnectionString = await CreateStartupDatabaseAsync();
        await using var orderAccumulatorApp = CreateOrderAccumulatorApp(startupDatabaseConnectionString, uniqueVersionTag, new OrderAccumulatorCapturedLogs());
        var appServices = orderAccumulatorApp.Services;
        var appOrderMetricsClient = (DogStatsdMetricsClient)appServices.GetRequiredService<IMetricsClient>();

        await appServices.DecideIncomingOrderAsync(TestOrders.NewBuyOrder("PETR4", 100, 10.50m));
        appOrderMetricsClient.FlushPendingMetrics();
        appOrderMetricsClient.IncrementCounter(SentinelMetricName, [$"version:{uniqueVersionTag}"]);
        appOrderMetricsClient.FlushPendingMetrics();
        var receivedOnTheAgentPort = await agentOnTheDatadogPort.ReadFlowaMetricsUntilAsync(SentinelMetricName);

        var acceptedOrderMetric = Assert.Single(receivedOnTheAgentPort, receivedMetric =>
            receivedMetric.MetricName == OrderMetricNames.AcceptedOrders && receivedMetric.MetricTags.Contains($"version:{uniqueVersionTag}"));
        Assert.Equal(
            new DogStatsdMetricLine(OrderMetricNames.AcceptedOrders, "1", "c",
                new SortedSet<string> { "env:dev", "service:order-accumulator", $"version:{uniqueVersionTag}", "symbol:PETR4", "side:buy" }),
            acceptedOrderMetric);
    }

    [Fact]
    public async Task Without_an_agent_the_app_processes_every_order_and_writes_only_the_accepted_order_lines()
    {
        var startupDatabaseConnectionString = await CreateStartupDatabaseAsync();
        var capturedAppLogs = new OrderAccumulatorCapturedLogs();
        await using var orderAccumulatorApp = CreateOrderAccumulatorApp(startupDatabaseConnectionString, "sha-without-agent", capturedAppLogs);
        var appServices = orderAccumulatorApp.Services;
        var appOrderMetricsClient = (DogStatsdMetricsClient)appServices.GetRequiredService<IMetricsClient>();
        var appOrderDecisionServices = appServices;
        var logLinesBeforeTheOrders = capturedAppLogs.CapturedLogLines.Count;

        var orderDecisionsWithoutAgent = new List<DecideIncomingOrderResponse>();
        for (var orderNumber = 0; orderNumber < 20; orderNumber++)
        {
            orderDecisionsWithoutAgent.Add(await appOrderDecisionServices.DecideIncomingOrderAsync(TestOrders.NewBuyOrder("PETR4", 1, 1m)));
            appOrderMetricsClient.FlushPendingMetrics();
        }
        appOrderMetricsClient.Dispose();

        Assert.Equal(20, orderDecisionsWithoutAgent.Count(orderDecision => orderDecision.Accepted));
        Assert.Equal(
            Enumerable.Repeat("Information Base.OrderAccumulator.Application.Orders.UseCases.DecideIncomingOrderUseCase: Order accepted.", 20),
            capturedAppLogs.CapturedLogLines.Skip(logLinesBeforeTheOrders));
        await using var startupDatabase = NpgsqlDataSource.Create(startupDatabaseConnectionString);
        await using var startupDatabaseConnection = await startupDatabase.OpenConnectionAsync();
        Assert.Equal(20m, await startupDatabaseConnection.ExecuteScalarAsync<decimal>("SELECT exposure FROM exposures WHERE symbol = 'PETR4'"));
    }

    [Fact]
    public async Task App_counts_orders_through_the_datadog_adapter_and_loads_the_stored_exposure_at_startup()
    {
        var startupDatabaseConnectionString = await CreateStartupDatabaseAsync("UPDATE exposures SET exposure = 4321.50 WHERE symbol = 'VALE3'");
        await using var orderAccumulatorApp = CreateOrderAccumulatorApp(startupDatabaseConnectionString, "test-sha", new OrderAccumulatorCapturedLogs());

        var appServices = orderAccumulatorApp.Services;
        Assert.IsType<DatadogOrderMetricsAdapter>(appServices.GetRequiredService<IOrderMetricsPort>());
        await using (var orderOperationScope = appServices.CreateAsyncScope())
            Assert.IsType<OrderRepository>(orderOperationScope.ServiceProvider.GetRequiredService<IOrderRepository>());
        Assert.Contains(appServices.GetServices<IHostedService>(), hostedService => hostedService is SymbolExposureGaugeBackgroundService);
        Assert.Equal(
            [new SymbolExposure("PETR4", 0m), new SymbolExposure("VALE3", 4321.50m), new SymbolExposure("VIIA4", 0m)],
            appServices.GetRequiredService<ISymbolExposureMemoryPort>().ReadCurrentSymbolExposures());
    }

    // Own database per app test: the exposure loaded at startup does not depend on the other tests.
    private async Task<string> CreateStartupDatabaseAsync(string? sqlBeforeTheAppStarts = null)
    {
        var startupDatabaseName = "metricas_" + Guid.NewGuid().ToString("N");
        await using (var postgresAdminConnection = await orderAccumulatorDatabase.OrderDatabaseDataSource.OpenConnectionAsync())
            await postgresAdminConnection.ExecuteAsync($"CREATE DATABASE {startupDatabaseName}");
        var startupDatabaseConnectionString = new NpgsqlConnectionStringBuilder(orderAccumulatorDatabase.OrderDatabaseConnectionString)
        {
            Database = startupDatabaseName
        }.ConnectionString;
        await using (var startupConnectionSource = new PostgresConnectionSource(startupDatabaseConnectionString))
            await startupConnectionSource.ApplyOrderAccumulatorSchemaAsync();
        await using var startupDatabase = NpgsqlDataSource.Create(startupDatabaseConnectionString);
        if (sqlBeforeTheAppStarts is not null)
        {
            await using var startupDatabaseConnection = await startupDatabase.OpenConnectionAsync();
            await startupDatabaseConnection.ExecuteAsync(sqlBeforeTheAppStarts);
        }

        return startupDatabaseConnectionString;
    }

    private static WebApplicationFactory<Program> CreateOrderAccumulatorApp(
        string startupDatabaseConnectionString, string versionTag, OrderAccumulatorCapturedLogs capturedAppLogs)
    {
        var orderAccumulatorApp = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(orderAccumulatorHost => orderAccumulatorHost
                .UseSetting("ConnectionStrings:Flowa", startupDatabaseConnectionString)
                .UseSetting("Fix:AcceptorPort", "0")
                .UseSetting("Fix:AcceptorBindHost", OrderAccumulatorFixTestHost.FixAcceptorLoopbackBindHost)
                .UseSetting("DD_ENV", "dev")
                .UseSetting("DD_SERVICE", "order-accumulator")
                .UseSetting("DD_VERSION", versionTag)
                .ConfigureLogging(orderAccumulatorLogging => orderAccumulatorLogging.AddProvider(capturedAppLogs)));
        orderAccumulatorApp.CreateClient();
        return orderAccumulatorApp;
    }

    // The gauge goes out in a send loop; reads until the three symbols arrive and sorts them by tag.
    private async Task<List<DogStatsdMetricLine>> ReadThreeExposureGaugesAsync()
    {
        using var gaugeWait = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var receivedExposureGauges = new List<DogStatsdMetricLine>();
        while (receivedExposureGauges.Count < 3)
        {
            gaugeWait.Token.ThrowIfCancellationRequested();
            receivedExposureGauges.AddRange((await SendSentinelAndReadOrderMetricsAsync())
                .Where(receivedMetric => receivedMetric.MetricName == OrderMetricNames.SymbolExposure));
        }

        return receivedExposureGauges.OrderBy(exposureGauge => exposureGauge.MetricTags.Single(metricTag => metricTag.StartsWith("symbol:"))).ToList();
    }

    private decimal ExposureInMemory(string orderSymbol) =>
        symbolExposureMemory.ReadCurrentSymbolExposures().Single(currentSymbolExposure => currentSymbolExposure.Symbol == orderSymbol).Exposure;

    private async Task SetStoredExposureAsync(string orderSymbol, decimal storedExposure)
    {
        await using (var orderDatabaseConnection = await orderAccumulatorDatabase.OrderDatabaseDataSource.OpenConnectionAsync())
            await orderDatabaseConnection.ExecuteAsync(
                "UPDATE exposures SET exposure = @StoredExposure WHERE symbol = @Symbol", new { StoredExposure = storedExposure, Symbol = orderSymbol });
        symbolExposureMemory.LoadStoredExposures(await orderAccumulatorDatabase.ExposureReader.GetSymbolExposuresAsync());
    }

    // The absence of a metric can only be proven with a send afterwards: everything that arrived before the sentinel is what was sent.
    private async Task<List<DogStatsdMetricLine>> SendSentinelAndReadOrderMetricsAsync()
    {
        orderMetricsClient.FlushPendingMetrics();
        orderMetricsClient.IncrementCounter(SentinelMetricName, []);
        orderMetricsClient.FlushPendingMetrics();
        var receivedFlowaMetrics = await dogStatsdUdpListener.ReadFlowaMetricsUntilAsync(SentinelMetricName);
        return receivedFlowaMetrics.Where(receivedFlowaMetric => receivedFlowaMetric.MetricName != SentinelMetricName).ToList();
    }

    private static IReadOnlySet<string> ExpectedTags(params string[] orderTags) =>
        new SortedSet<string>(UnifiedServiceTags.Concat(orderTags));

    private static IConfiguration UnifiedServiceConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DD_ENV"] = "dev",
                ["DD_SERVICE"] = "order-accumulator",
                ["DD_VERSION"] = "test-sha"
            })
            .Build();

    private sealed class OrderRepositoryFailingWith(Exception databaseFailure) : IOrderRepository
    {
        public Task<Order?> FindOrderByClOrdIdAsync(string clOrdId, CancellationToken cancellationToken = default) =>
            Task.FromException<Order?>(databaseFailure);

        public Task<bool> TryAddOrderAsync(Order answeredOrder, CancellationToken cancellationToken = default) =>
            Task.FromException<bool>(databaseFailure);

        public Task DeleteAllOrdersAsync(CancellationToken cancellationToken = default) => Task.FromException(databaseFailure);
    }

    // A clock that only moves when the test says so: the gauge PeriodicTimer asks for its timer here.
    private sealed class ManualGaugeClock : TimeProvider
    {
        private ManualGaugeTimer? gaugeTimer;

        public TimeSpan GaugeTimerDueTime => gaugeTimer!.DueTime;
        public TimeSpan GaugeTimerPeriod => gaugeTimer!.Period;

        public override ITimer CreateTimer(TimerCallback timerCallback, object? timerState, TimeSpan dueTime, TimeSpan period) =>
            gaugeTimer = new ManualGaugeTimer(timerCallback, timerState, dueTime, period);

        public void TickGaugeTimer() => gaugeTimer!.RunGaugeTimerCallback();
    }

    private sealed class ManualGaugeTimer(TimerCallback timerCallback, object? timerState, TimeSpan dueTime, TimeSpan period) : ITimer
    {
        public TimeSpan DueTime { get; private set; } = dueTime;
        public TimeSpan Period { get; private set; } = period;

        public void RunGaugeTimerCallback() => timerCallback(timerState);

        public bool Change(TimeSpan newDueTime, TimeSpan newPeriod)
        {
            DueTime = newDueTime;
            Period = newPeriod;
            return true;
        }

        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

// One DogStatsD line: "name:value|type|#tag,tag". The tags become a sorted set
// so the comparison does not depend on the order in which the client writes them.
public sealed record DogStatsdMetricLine(string MetricName, string MetricValue, string MetricType, IReadOnlySet<string> MetricTags)
{
    public static DogStatsdMetricLine ParseDogStatsdLine(string dogStatsdLine)
    {
        var dogStatsdFields = dogStatsdLine.Split('|');
        var metricNameAndValue = dogStatsdFields[0].Split(':', 2);
        var metricTagsField = dogStatsdFields.Skip(2).FirstOrDefault(dogStatsdField => dogStatsdField.StartsWith('#'));
        var metricTags = metricTagsField is null ? [] : metricTagsField[1..].Split(',');
        return new DogStatsdMetricLine(metricNameAndValue[0], metricNameAndValue[1], dogStatsdFields[1], new SortedSet<string>(metricTags));
    }

    public bool Equals(DogStatsdMetricLine? otherMetricLine) =>
        otherMetricLine is not null
        && MetricName == otherMetricLine.MetricName
        && MetricValue == otherMetricLine.MetricValue
        && MetricType == otherMetricLine.MetricType
        && MetricTags.SetEquals(otherMetricLine.MetricTags);

    public override int GetHashCode() => HashCode.Combine(MetricName, MetricValue, MetricType);

    public override string ToString() => $"{MetricName}:{MetricValue}|{MetricType}|#{string.Join(',', MetricTags)}";
}

// Plays the agent: listens to UDP on IPv4 and IPv6, because "localhost" may resolve to either one.
public sealed class DogStatsdUdpListener : IDisposable
{
    private static readonly TimeSpan SentinelWaitLimit = TimeSpan.FromSeconds(15);
    private readonly UdpClient dogStatsdUdpClient;

    public DogStatsdUdpListener(int listenerPort = 0)
    {
        dogStatsdUdpClient = new UdpClient(AddressFamily.InterNetworkV6);
        dogStatsdUdpClient.Client.DualMode = true;
        dogStatsdUdpClient.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, listenerPort));
    }

    public int ListenerPort => ((IPEndPoint)dogStatsdUdpClient.Client.LocalEndPoint!).Port;

    public async Task<List<DogStatsdMetricLine>> ReadFlowaMetricsUntilAsync(string sentinelMetricName)
    {
        using var sentinelWait = new CancellationTokenSource(SentinelWaitLimit);
        var receivedFlowaMetrics = new List<DogStatsdMetricLine>();
        while (!receivedFlowaMetrics.Any(receivedFlowaMetric => receivedFlowaMetric.MetricName == sentinelMetricName))
        {
            var receivedDatagram = await dogStatsdUdpClient.ReceiveAsync(sentinelWait.Token);
            receivedFlowaMetrics.AddRange(Encoding.UTF8.GetString(receivedDatagram.Buffer)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(dogStatsdLine => dogStatsdLine.StartsWith("flowa."))
                .Select(DogStatsdMetricLine.ParseDogStatsdLine));
        }

        return receivedFlowaMetrics;
    }

    public void Dispose() => dogStatsdUdpClient.Dispose();
}
