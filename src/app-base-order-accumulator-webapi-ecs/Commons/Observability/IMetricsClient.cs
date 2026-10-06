namespace Base.OrderAccumulator.Commons.Observability;

public interface IMetricsClient
{
    void IncrementCounter(string metricName, string[] metricTags);

    void RecordGauge(string metricName, double gaugeValue, string[] metricTags);
}
