namespace Flowa.Commons.Observability;

public interface IMonitoredOperation : IDisposable
{
    void RecordOperationResult(string operationResult);
}
