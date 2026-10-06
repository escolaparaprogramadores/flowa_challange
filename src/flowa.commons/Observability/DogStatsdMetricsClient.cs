using StatsdClient;

namespace Flowa.Commons.Observability;

public sealed class DogStatsdMetricsClient : IMetricsClient, IDisposable
{
    private readonly DogStatsdService dogStatsdService = new();

    public DogStatsdMetricsClient(string agentHost, int agentDogStatsdPort, string? serviceEnvironment, string? serviceName, string? serviceVersion)
    {
        dogStatsdService.Configure(
            new StatsdConfig
            {
                StatsdServerName = agentHost,
                StatsdPort = agentDogStatsdPort,
                Environment = serviceEnvironment,
                ServiceName = serviceName,
                ServiceVersion = serviceVersion
            },
            IgnoreDogStatsdSendFailure);
    }

    public void IncrementCounter(string metricName, string[] metricTags) => dogStatsdService.Increment(metricName, tags: metricTags);

    public void RecordGauge(string metricName, double gaugeValue, string[] metricTags) => dogStatsdService.Gauge(metricName, gaugeValue, tags: metricTags);

    public void FlushPendingMetrics() => dogStatsdService.Flush();

    public void Dispose() => dogStatsdService.Dispose();

    private static void IgnoreDogStatsdSendFailure(Exception dogStatsdSendFailure) { }
}
