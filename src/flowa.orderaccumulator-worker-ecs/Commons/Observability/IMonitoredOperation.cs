namespace Flowa.OrderAccumulator.Commons.Observability;

public interface IMonitoredOperation : IDisposable
{
    void RecordOperationResult(string operationResult);
}
