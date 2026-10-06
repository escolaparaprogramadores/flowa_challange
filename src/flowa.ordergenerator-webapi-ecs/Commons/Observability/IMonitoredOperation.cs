namespace Flowa.OrderGenerator.Commons.Observability;

public interface IMonitoredOperation : IDisposable
{
    void RecordOperationResult(string operationResult);
}
