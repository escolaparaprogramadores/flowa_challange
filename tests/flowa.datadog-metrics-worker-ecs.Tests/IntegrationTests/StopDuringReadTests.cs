using Dapper;
using Flowa.DatadogMetrics.Application.Exposures.UseCases;
using Flowa.DatadogMetrics.Entrypoint.BackgroundService;
using Npgsql;

namespace Flowa.DatadogMetrics.Tests;

[Collection(DatadogMetricsPostgresCollection.Name)]
public sealed class StopDuringReadTests(DatadogMetricsPostgresFixture datadogMetricsDatabase) : IDisposable
{
    private readonly DogStatsdUdpListener dogStatsdUdpListener = new();

    [Fact]
    public async Task Stopping_the_worker_while_it_waits_on_the_database_ends_the_loop_without_an_error_line()
    {
        // Arrange
        var flowaTestDatabase = await datadogMetricsDatabase.CreateFlowaDatabaseWithTheThreeExposuresAsync(petr4Exposure: 10m);
        await using var exposuresLockConnection = new NpgsqlConnection(flowaTestDatabase.FlowaConnectionString);
        await exposuresLockConnection.OpenAsync();
        await using var exposuresLockTransaction = await exposuresLockConnection.BeginTransactionAsync();
        await exposuresLockConnection.ExecuteAsync("LOCK TABLE exposures IN ACCESS EXCLUSIVE MODE", transaction: exposuresLockTransaction);
        await using var datadogMetricsWorker = new DatadogMetricsTestWorker(flowaTestDatabase.FlowaConnectionString, dogStatsdUdpListener, new ManualMetricsClock());
        var metricsBackgroundService = datadogMetricsWorker.MetricsBackgroundService;
        var metricsLoopLogger = datadogMetricsWorker.GetRecordingLoggerOf<DatadogMetricsBackgroundService>();
        var exposureGaugesLogger = datadogMetricsWorker.GetRecordingLoggerOf<SendSymbolExposureGaugesUseCase>();

        // Act
        await metricsBackgroundService.StartAsync(CancellationToken.None);
        var exposureReadWaitingOnTheLock = await WaitForAQueryBlockedOnALockAsync(flowaTestDatabase.FlowaConnectionString);
        await metricsBackgroundService.StopAsync(CancellationToken.None);
        await exposuresLockTransaction.RollbackAsync();
        var sentMetrics = await datadogMetricsWorker.SendSentinelAndReadSentMetricsAsync();

        // Assert
        Assert.Contains("FROM exposures", exposureReadWaitingOnTheLock);
        Assert.True(metricsBackgroundService.ExecuteTask!.IsCompleted);
        Assert.Equal(["Information Datadog metrics loop started."], metricsLoopLogger.RecordedLogLines);
        Assert.Empty(exposureGaugesLogger.RecordedLogLines);
        Assert.Empty(sentMetrics);
    }

    // Outside any transaction: inside one, pg_stat_activity keeps the snapshot of the first read and never shows the new wait.
    private static async Task<string> WaitForAQueryBlockedOnALockAsync(string flowaConnectionString)
    {
        await using var activityObserverConnection = new NpgsqlConnection(flowaConnectionString);
        await activityObserverConnection.OpenAsync();
        using var blockedQueryWait = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            var blockedQuery = await activityObserverConnection.QueryFirstOrDefaultAsync<string>(
                "SELECT query FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND pid <> pg_backend_pid()");
            if (blockedQuery is not null)
                return blockedQuery;
            await Task.Delay(TimeSpan.FromMilliseconds(100), blockedQueryWait.Token);
        }
    }

    public void Dispose() => dogStatsdUdpListener.Dispose();
}
