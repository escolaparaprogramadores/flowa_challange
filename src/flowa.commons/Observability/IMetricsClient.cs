namespace Flowa.Commons.Observability;

public interface IMetricsClient
{
    void IncrementCounter(string metricName, string[] metricTags);

    void IncrementCounter(string metricName, long counterIncrement, string[] metricTags);

    void RecordGauge(string metricName, double gaugeValue, string[] metricTags);
}
