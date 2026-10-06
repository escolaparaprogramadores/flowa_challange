namespace Flowa.OrderAccumulator.Commons.Logging;

public interface IApplicationLogger<T>
{
    void LogInformation(string message, object? context = null);
    void LogWarning(string message, object? context = null);
    void LogError(Exception exception, string message, object? context = null);
}
