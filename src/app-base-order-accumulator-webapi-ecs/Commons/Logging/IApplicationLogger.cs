namespace Base.OrderAccumulator.Commons;

// The only way the application logs (backend-observabilidade.md). The implementation lives in the
// Infrastructure and puts the trace id of the current span on every line.
public interface IApplicationLogger<T>
{
    void LogInformation(string message, object? context = null);
    void LogWarning(string message, object? context = null);
    void LogError(Exception exception, string message, object? context = null);
}
