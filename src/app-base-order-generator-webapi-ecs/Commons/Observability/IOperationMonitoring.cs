namespace Base.OrderGenerator.Commons.Observability;

public interface IOperationMonitoring
{
    IMonitoredOperation StartOperationMonitoring(string operationName);
}
