namespace Flowa.Commons.Observability;

public interface IOperationMonitoring
{
    IMonitoredOperation StartOperationMonitoring(string operationName);
}
