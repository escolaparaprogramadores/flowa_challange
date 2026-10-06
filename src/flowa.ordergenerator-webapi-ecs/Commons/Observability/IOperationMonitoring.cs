namespace Flowa.OrderGenerator.Commons.Observability;

public interface IOperationMonitoring
{
    IMonitoredOperation StartOperationMonitoring(string operationName);
}
