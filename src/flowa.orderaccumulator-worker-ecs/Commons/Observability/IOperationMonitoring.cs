namespace Flowa.OrderAccumulator.Commons.Observability;

public interface IOperationMonitoring
{
    IMonitoredOperation StartOperationMonitoring(string operationName);
}
