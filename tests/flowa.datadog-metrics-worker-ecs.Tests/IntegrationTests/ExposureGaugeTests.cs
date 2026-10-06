using Flowa.DatadogMetrics.Application.Exposures.UseCases;
using Flowa.DatadogMetrics.Entrypoint.BackgroundService;

namespace Flowa.DatadogMetrics.Tests;

[Collection(DatadogMetricsPostgresCollection.Name)]
public sealed class ExposureGaugeTests(DatadogMetricsPostgresFixture datadogMetricsDatabase) : IDisposable
{
    private readonly DogStatsdUdpListener dogStatsdUdpListener = new();

    [Fact]
    public async Task Exposure_gauge_sends_the_value_stored_in_the_database_for_each_of_the_three_symbols()
    {
        // Arrange
        var flowaTestDatabase = await datadogMetricsDatabase.CreateFlowaDatabaseWithTheThreeExposuresAsync(
            petr4Exposure: 12340m, vale3Exposure: -15030m, viia4Exposure: 0m);
        await using var datadogMetricsWorker = new DatadogMetricsTestWorker(flowaTestDatabase.FlowaConnectionString, dogStatsdUdpListener, TimeProvider.System);

        // Act
        var sentMetrics = await datadogMetricsWorker.RunOneMetricsCycleAndReadSentMetricsAsync();

        // Assert
        Assert.Equal(
            DatadogMetricsTestWorker.SortMetricLinesForComparison([
                new DogStatsdMetricLine("flowa.exposicao", "12340", "g", DatadogMetricsTestWorker.BuildExpectedMetricTags("symbol:PETR4")),
                new DogStatsdMetricLine("flowa.exposicao", "-15030", "g", DatadogMetricsTestWorker.BuildExpectedMetricTags("symbol:VALE3")),
                new DogStatsdMetricLine("flowa.exposicao", "0", "g", DatadogMetricsTestWorker.BuildExpectedMetricTags("symbol:VIIA4"))
            ]),
            DatadogMetricsTestWorker.SortMetricLinesForComparison(sentMetrics));
    }

    [Fact]
    public async Task Exposure_gauge_follows_the_database_from_one_cycle_to_the_next()
    {
        // Arrange
        var flowaTestDatabase = await datadogMetricsDatabase.CreateFlowaDatabaseWithTheThreeExposuresAsync(petr4Exposure: 100m);
        await using var datadogMetricsWorker = new DatadogMetricsTestWorker(flowaTestDatabase.FlowaConnectionString, dogStatsdUdpListener, TimeProvider.System);
        await datadogMetricsWorker.RunOneMetricsCycleAndReadSentMetricsAsync();
        await flowaTestDatabase.StoreExposureAsync("PETR4", 2500.75m);

        // Act
        var sentMetricsAfterTheChange = await datadogMetricsWorker.RunOneMetricsCycleAndReadSentMetricsAsync();

        // Assert
        Assert.Equal(
            DatadogMetricsTestWorker.SortMetricLinesForComparison([
                new DogStatsdMetricLine("flowa.exposicao", "2500.75", "g", DatadogMetricsTestWorker.BuildExpectedMetricTags("symbol:PETR4")),
                new DogStatsdMetricLine("flowa.exposicao", "0", "g", DatadogMetricsTestWorker.BuildExpectedMetricTags("symbol:VALE3")),
                new DogStatsdMetricLine("flowa.exposicao", "0", "g", DatadogMetricsTestWorker.BuildExpectedMetricTags("symbol:VIIA4"))
            ]),
            DatadogMetricsTestWorker.SortMetricLinesForComparison(sentMetricsAfterTheChange));
    }

    [Fact]
    public async Task Metrics_loop_sends_at_startup_and_again_only_when_the_5_minute_timer_ticks()
    {
        // Arrange
        var flowaTestDatabase = await datadogMetricsDatabase.CreateFlowaDatabaseWithTheThreeExposuresAsync(petr4Exposure: 20m);
        var manualMetricsClock = new ManualMetricsClock();
        await using var datadogMetricsWorker = new DatadogMetricsTestWorker(flowaTestDatabase.FlowaConnectionString, dogStatsdUdpListener, manualMetricsClock);
        var metricsLoopLogger = datadogMetricsWorker.GetRecordingLoggerOf<DatadogMetricsBackgroundService>();
        var exposureGaugesLogger = datadogMetricsWorker.GetRecordingLoggerOf<SendSymbolExposureGaugesUseCase>();
        var expectedExposureGauges = DatadogMetricsTestWorker.SortMetricLinesForComparison(
        [
            new DogStatsdMetricLine("flowa.exposicao", "20", "g", DatadogMetricsTestWorker.BuildExpectedMetricTags("symbol:PETR4")),
            new DogStatsdMetricLine("flowa.exposicao", "0", "g", DatadogMetricsTestWorker.BuildExpectedMetricTags("symbol:VALE3")),
            new DogStatsdMetricLine("flowa.exposicao", "0", "g", DatadogMetricsTestWorker.BuildExpectedMetricTags("symbol:VIIA4"))
        ]);

        // Act
        await datadogMetricsWorker.MetricsBackgroundService.StartAsync(CancellationToken.None);
        await exposureGaugesLogger.WaitForLogLineCountAsync(1);
        var gaugesAtStartup = await datadogMetricsWorker.SendSentinelAndReadSentMetricsAsync();
        var gaugesBeforeTheTick = await datadogMetricsWorker.SendSentinelAndReadSentMetricsAsync();
        manualMetricsClock.TickMetricsTimer();
        await exposureGaugesLogger.WaitForLogLineCountAsync(2);
        var gaugesAfterTheTick = await datadogMetricsWorker.SendSentinelAndReadSentMetricsAsync();
        await datadogMetricsWorker.MetricsBackgroundService.StopAsync(CancellationToken.None);

        // Assert
        Assert.Equal(TimeSpan.FromMinutes(5), manualMetricsClock.MetricsTimerDueTime);
        Assert.Equal(TimeSpan.FromMinutes(5), manualMetricsClock.MetricsTimerPeriod);
        Assert.Equal(expectedExposureGauges, DatadogMetricsTestWorker.SortMetricLinesForComparison(gaugesAtStartup));
        Assert.Empty(gaugesBeforeTheTick);
        Assert.Equal(expectedExposureGauges, DatadogMetricsTestWorker.SortMetricLinesForComparison(gaugesAfterTheTick));
        Assert.Equal(["Information Datadog metrics loop started."], metricsLoopLogger.RecordedLogLines);
        Assert.Equal(["Information Symbol exposure gauges sent.", "Information Symbol exposure gauges sent."], exposureGaugesLogger.RecordedLogLines);
        Assert.All(exposureGaugesLogger.RecordedInformationContexts, exposureGaugesSentContext =>
            Assert.Equal("PETR4=20 VALE3=0 VIIA4=0", exposureGaugesSentContext.GetProperty("SentExposures").GetString()));
    }

    public void Dispose() => dogStatsdUdpListener.Dispose();
}
