using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Base.OrderAccumulator.Commons.Observability;

public sealed class ActiveSpanOperationMonitoring : IOperationMonitoring
{
    public const string OperationMeterName = "Base.OrderAccumulator";
    public const string OperationDurationMetricName = "orderaccumulator.usecase.duration";
    public const string OperationNameTag = "usecase";
    public const string OperationResultTag = "result";
    public const string OperationDurationTag = "duration_ms";

    private readonly Histogram<double> operationDurations;

    public ActiveSpanOperationMonitoring(IMeterFactory meterFactory)
    {
        var receivedMeterFactory = meterFactory ?? throw new ArgumentNullException(nameof(meterFactory));
        operationDurations = receivedMeterFactory.Create(OperationMeterName).CreateHistogram<double>(
            OperationDurationMetricName, unit: "s", description: "Duration of each use case, with its result.");
    }

    public IMonitoredOperation StartOperationMonitoring(string operationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        return new MonitoredOperation(operationName, operationDurations);
    }

    private sealed class MonitoredOperation : IMonitoredOperation
    {
        private readonly string operationName;
        private readonly Histogram<double> operationDurations;
        private readonly long operationStart = Stopwatch.GetTimestamp();
        private string operationResult = OperationResults.Succeeded;

        public MonitoredOperation(string operationName, Histogram<double> operationDurations)
        {
            this.operationName = operationName ?? throw new ArgumentNullException(nameof(operationName));
            this.operationDurations = operationDurations ?? throw new ArgumentNullException(nameof(operationDurations));
        }

        public void RecordOperationResult(string operationResult)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(operationResult);
            this.operationResult = operationResult;
        }

        public void Dispose()
        {
            var operationDuration = Stopwatch.GetElapsedTime(operationStart);
            Activity.Current?.SetTag(OperationNameTag, operationName)
                .SetTag(OperationResultTag, operationResult)
                .SetTag(OperationDurationTag, operationDuration.TotalMilliseconds);
            operationDurations.Record(
                operationDuration.TotalSeconds,
                new KeyValuePair<string, object?>(OperationNameTag, operationName),
                new KeyValuePair<string, object?>(OperationResultTag, operationResult));
        }
    }
}
