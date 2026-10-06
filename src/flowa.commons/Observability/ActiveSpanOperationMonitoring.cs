using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Flowa.Commons.Observability;

public sealed class ActiveSpanOperationMonitoring : IOperationMonitoring
{
    public const string OperationNameTag = "usecase";
    public const string OperationResultTag = "result";
    public const string OperationDurationTag = "duration_ms";

    private readonly Histogram<double> _operationDurations;

    public ActiveSpanOperationMonitoring(IMeterFactory meterFactory, string operationMeterName, string operationDurationMetricName)
    {
        var receivedMeterFactory = meterFactory ?? throw new ArgumentNullException(nameof(meterFactory));
        ArgumentException.ThrowIfNullOrWhiteSpace(operationMeterName);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationDurationMetricName);
        _operationDurations = receivedMeterFactory.Create(operationMeterName).CreateHistogram<double>(
            operationDurationMetricName, unit: "s", description: "Duration of each use case, with its result.");
    }

    public IMonitoredOperation StartOperationMonitoring(string operationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        return new MonitoredOperation(operationName, _operationDurations);
    }

    private sealed class MonitoredOperation(string operationName, Histogram<double> operationDurations) : IMonitoredOperation
    {
        private readonly long _operationStart = Stopwatch.GetTimestamp();
        private string _operationResult = OperationResults.Succeeded;

        public void RecordOperationResult(string operationResult)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(operationResult);
            _operationResult = operationResult;
        }

        public void Dispose()
        {
            var operationDuration = Stopwatch.GetElapsedTime(_operationStart);
            Activity.Current?.SetTag(OperationNameTag, operationName)
                .SetTag(OperationResultTag, _operationResult)
                .SetTag(OperationDurationTag, operationDuration.TotalMilliseconds);
            operationDurations.Record(
                operationDuration.TotalSeconds,
                new KeyValuePair<string, object?>(OperationNameTag, operationName),
                new KeyValuePair<string, object?>(OperationResultTag, _operationResult));
        }
    }
}
