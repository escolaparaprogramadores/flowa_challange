using System.Net;
using System.Net.Sockets;
using Flowa.DatadogMetrics.Entrypoint.BackgroundService;

namespace Flowa.DatadogMetrics.Tests;

[Collection(DatadogMetricsPostgresCollection.Name)]
public sealed class DatabaseFailureTests(DatadogMetricsPostgresFixture datadogMetricsDatabase) : IDisposable
{
    private const string ExposureGaugesNotSentLogLine = "Error Symbol exposure gauges not sent. Trying again on the next cycle.";
    private const string OrderCountsNotSentLogLine = "Error Answered order counts not sent. Trying again on the next cycle.";

    private readonly DogStatsdUdpListener dogStatsdUdpListener = new();

    [Fact]
    public async Task Missing_exposure_rows_are_logged_the_order_count_still_starts_and_the_next_cycle_sends_the_gauge()
    {
        // Arrange
        var flowaTestDatabase = await datadogMetricsDatabase.CreateEmptyFlowaDatabaseAsync();
        await flowaTestDatabase.ApplyAccumulatorSchemaAsync();
        await flowaTestDatabase.StoreExposureAsync("PETR4", 300m);
        await using var datadogMetricsWorker = new DatadogMetricsTestWorker(flowaTestDatabase.FlowaConnectionString, dogStatsdUdpListener, TimeProvider.System);
        var metricsLoopLogger = datadogMetricsWorker.GetRecordingLoggerOf<DatadogMetricsBackgroundService>();

        // Act
        var metricsWithoutTheRows = await datadogMetricsWorker.RunOneMetricsCycleAndReadSentMetricsAsync();
        await flowaTestDatabase.StoreExposureAsync("VALE3", 0m);
        await flowaTestDatabase.StoreExposureAsync("VIIA4", 0m);
        await flowaTestDatabase.StoreAnsweredOrderAsync("PETR4", FlowaTestDatabase.BuyOrderSide, accepted: true);
        var metricsWithTheRows = await datadogMetricsWorker.RunOneMetricsCycleAndReadSentMetricsAsync();

        // Assert
        Assert.Empty(metricsWithoutTheRows);
        Assert.Equal([ExposureGaugesNotSentLogLine], metricsLoopLogger.RecordedLogLines);
        Assert.Equal(
            DatadogMetricsTestWorker.SortMetricLinesForComparison([
                new DogStatsdMetricLine("flowa.exposicao", "300", "g", DatadogMetricsTestWorker.BuildExpectedMetricTags("symbol:PETR4")),
                new DogStatsdMetricLine("flowa.exposicao", "0", "g", DatadogMetricsTestWorker.BuildExpectedMetricTags("symbol:VALE3")),
                new DogStatsdMetricLine("flowa.exposicao", "0", "g", DatadogMetricsTestWorker.BuildExpectedMetricTags("symbol:VIIA4")),
                new DogStatsdMetricLine("flowa.ordens.aceitas", "1", "c", DatadogMetricsTestWorker.BuildExpectedMetricTags("symbol:PETR4", "side:buy"))
            ]),
            DatadogMetricsTestWorker.SortMetricLinesForComparison(metricsWithTheRows));
    }

    [Fact]
    public async Task Database_without_the_tables_is_logged_and_once_they_exist_the_worker_sends_without_counting_what_was_already_there()
    {
        // Arrange
        var flowaTestDatabase = await datadogMetricsDatabase.CreateEmptyFlowaDatabaseAsync();
        await using var datadogMetricsWorker = new DatadogMetricsTestWorker(flowaTestDatabase.FlowaConnectionString, dogStatsdUdpListener, TimeProvider.System);
        var metricsLoopLogger = datadogMetricsWorker.GetRecordingLoggerOf<DatadogMetricsBackgroundService>();

        // Act
        var metricsWithoutTables = await datadogMetricsWorker.RunOneMetricsCycleAndReadSentMetricsAsync();
        await flowaTestDatabase.ApplyAccumulatorSchemaAsync();
        await flowaTestDatabase.StoreExposureAsync("PETR4", 0m);
        await flowaTestDatabase.StoreExposureAsync("VALE3", 0m);
        await flowaTestDatabase.StoreExposureAsync("VIIA4", 0m);
        await flowaTestDatabase.StoreAnsweredOrderAsync("VALE3", FlowaTestDatabase.SellOrderSide, accepted: true);
        var metricsOnceTheTablesExist = await datadogMetricsWorker.RunOneMetricsCycleAndReadSentMetricsAsync();
        await flowaTestDatabase.StoreAnsweredOrderAsync("VIIA4", FlowaTestDatabase.BuyOrderSide, accepted: false);
        var metricsOnTheNextCycle = await datadogMetricsWorker.RunOneMetricsCycleAndReadSentMetricsAsync();

        // Assert
        Assert.Empty(metricsWithoutTables);
        Assert.Equal([ExposureGaugesNotSentLogLine, OrderCountsNotSentLogLine], metricsLoopLogger.RecordedLogLines);
        Assert.Equal(
            DatadogMetricsTestWorker.SortMetricLinesForComparison([
                new DogStatsdMetricLine("flowa.exposicao", "0", "g", DatadogMetricsTestWorker.BuildExpectedMetricTags("symbol:PETR4")),
                new DogStatsdMetricLine("flowa.exposicao", "0", "g", DatadogMetricsTestWorker.BuildExpectedMetricTags("symbol:VALE3")),
                new DogStatsdMetricLine("flowa.exposicao", "0", "g", DatadogMetricsTestWorker.BuildExpectedMetricTags("symbol:VIIA4"))
            ]),
            DatadogMetricsTestWorker.SortMetricLinesForComparison(metricsOnceTheTablesExist));
        Assert.Equal(
            [new DogStatsdMetricLine("flowa.ordens.rejeitadas", "1", "c", DatadogMetricsTestWorker.BuildExpectedMetricTags("symbol:VIIA4", "side:buy"))],
            metricsOnTheNextCycle.Where(sentMetric => sentMetric.MetricName.StartsWith("flowa.ordens.")));
    }

    [Fact]
    public async Task Unreachable_database_is_logged_on_every_tick_and_the_metrics_loop_keeps_running()
    {
        // Arrange
        var manualMetricsClock = new ManualMetricsClock();
        await using var datadogMetricsWorker = new DatadogMetricsTestWorker(
            BuildUnreachableFlowaConnectionString(), dogStatsdUdpListener, manualMetricsClock);
        var metricsBackgroundService = datadogMetricsWorker.MetricsBackgroundService;
        var metricsLoopLogger = datadogMetricsWorker.GetRecordingLoggerOf<DatadogMetricsBackgroundService>();

        // Act
        await metricsBackgroundService.StartAsync(CancellationToken.None);
        await metricsLoopLogger.WaitForLogLineCountAsync(3);
        manualMetricsClock.TickMetricsTimer();
        await metricsLoopLogger.WaitForLogLineCountAsync(5);
        manualMetricsClock.TickMetricsTimer();
        await metricsLoopLogger.WaitForLogLineCountAsync(7);
        var sentMetrics = await datadogMetricsWorker.SendSentinelAndReadSentMetricsAsync();
        var metricsLoopStillRunning = !metricsBackgroundService.ExecuteTask!.IsCompleted;
        await metricsBackgroundService.StopAsync(CancellationToken.None);

        // Assert
        Assert.True(metricsLoopStillRunning);
        Assert.Empty(sentMetrics);
        Assert.Equal(
            [
                "Information Datadog metrics loop started.",
                ExposureGaugesNotSentLogLine, OrderCountsNotSentLogLine,
                ExposureGaugesNotSentLogLine, OrderCountsNotSentLogLine,
                ExposureGaugesNotSentLogLine, OrderCountsNotSentLogLine
            ],
            metricsLoopLogger.RecordedLogLines);
    }

    private static string BuildUnreachableFlowaConnectionString()
    {
        var closedPortListener = new TcpListener(IPAddress.Loopback, 0);
        closedPortListener.Start();
        var closedPort = ((IPEndPoint)closedPortListener.LocalEndpoint).Port;
        closedPortListener.Stop();
        return $"Host=127.0.0.1;Port={closedPort};Database=flowa;Username=flowa;Timeout=2";
    }

    public void Dispose() => dogStatsdUdpListener.Dispose();
}
