using Flowa.Commons.DependencyInjection;
using Flowa.Commons.Logging;
using Flowa.Commons.Observability;
using Flowa.DatadogMetrics.Application.Exposures.UseCases;
using Flowa.DatadogMetrics.Application.Orders.UseCases;
using Flowa.DatadogMetrics.Entrypoint.BackgroundService;
using Flowa.DatadogMetrics.Entrypoint.Observability;
using Flowa.DatadogMetrics.Infrastructure.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Flowa.DatadogMetrics.Tests;

// The worker as Program wires it, with the DogStatsD client pointed at a local UDP listener in place of the
// agent and each log line recorded. A new instance is a restart: nothing but the database survives.
public sealed class DatadogMetricsTestWorker : IAsyncDisposable
{
    public const string SentinelMetricName = "flowa.test.sentinel";
    public static readonly string[] UnifiedServiceTags = ["env:dev", "service:datadog-metrics", "version:test-sha"];

    private readonly ServiceProvider workerServices;
    private readonly DogStatsdMetricsClient datadogMetricsClient;
    private readonly DogStatsdUdpListener dogStatsdUdpListener;

    public DatadogMetricsTestWorker(string flowaConnectionString, DogStatsdUdpListener dogStatsdUdpListener, TimeProvider metricsClock)
    {
        this.dogStatsdUdpListener = dogStatsdUdpListener;
        var unifiedServiceConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DD_ENV"] = "dev",
                ["DD_SERVICE"] = "datadog-metrics",
                ["DD_VERSION"] = "test-sha"
            })
            .Build();
        datadogMetricsClient = DatadogMetricsInfrastructureExtensions.CreateDatadogMetricsClient(dogStatsdUdpListener.ListenerPort, unifiedServiceConfiguration);

        var workerServiceCollection = new ServiceCollection();
        workerServiceCollection.AddMetrics();
        workerServiceCollection.AddSingleton(typeof(IApplicationLogger<>), typeof(RecordingApplicationLogger<>));
        workerServiceCollection.AddOperationMonitoring(DatadogMetricsUseCaseDurationMetric.MeterName, DatadogMetricsUseCaseDurationMetric.MetricName);
        workerServiceCollection.AddDatadogMetricsInfrastructure(flowaConnectionString, unifiedServiceConfiguration);
        workerServiceCollection.AddSingleton<IMetricsClient>(datadogMetricsClient);
        workerServiceCollection.AddSingleton(metricsClock);
        workerServiceCollection.AddScoped<SendSymbolExposureGaugesUseCase>();
        workerServiceCollection.AddScoped<SendAnsweredOrderCountsUseCase>();
        workerServiceCollection.AddSingleton<DatadogMetricsBackgroundService>();
        workerServices = workerServiceCollection.BuildServiceProvider();
    }

    public DatadogMetricsBackgroundService MetricsBackgroundService => workerServices.GetRequiredService<DatadogMetricsBackgroundService>();

    public RecordingApplicationLogger<T> LoggerOf<T>() => (RecordingApplicationLogger<T>)workerServices.GetRequiredService<IApplicationLogger<T>>();

    public async Task<List<DogStatsdMetricLine>> RunOneMetricsCycleAndReadSentMetricsAsync()
    {
        await MetricsBackgroundService.SendDatadogMetricsAsync(CancellationToken.None);
        return await SendSentinelAndReadSentMetricsAsync();
    }

    // The absence of a metric can only be proven with a send afterwards: everything that arrived before the sentinel is what was sent.
    public async Task<List<DogStatsdMetricLine>> SendSentinelAndReadSentMetricsAsync()
    {
        datadogMetricsClient.FlushPendingMetrics();
        datadogMetricsClient.IncrementCounter(SentinelMetricName, []);
        datadogMetricsClient.FlushPendingMetrics();
        var receivedFlowaMetrics = await dogStatsdUdpListener.ReadFlowaMetricsUntilAsync(SentinelMetricName);
        return receivedFlowaMetrics.Where(receivedFlowaMetric => receivedFlowaMetric.MetricName != SentinelMetricName).ToList();
    }

    public static IReadOnlySet<string> ExpectedTags(params string[] metricTags) =>
        new SortedSet<string>(UnifiedServiceTags.Concat(metricTags));

    // The client sends the lines in no fixed order: both sides of a comparison go through the same sort.
    public static List<DogStatsdMetricLine> InComparisonOrder(IEnumerable<DogStatsdMetricLine> metricLines) =>
        metricLines.OrderBy(metricLine => metricLine.ToString(), StringComparer.Ordinal).ToList();

    public async ValueTask DisposeAsync()
    {
        await workerServices.DisposeAsync();
        datadogMetricsClient.Dispose();
    }
}
