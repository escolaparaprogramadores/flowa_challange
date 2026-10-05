using Base.OrderAccumulator.Commons;

namespace Base.OrderAccumulator.Infrastructure.Logging;

// Writes through the framework logger, which prints one JSON line per event. The context goes in as a
// scope, so each of its fields becomes a JSON field of the line; fields without a value are left out.
public sealed class ApplicationLogger<T>(ILogger<T> frameworkLogger) : IApplicationLogger<T>
{
    public void LogInformation(string message, object? context = null) => WriteLogLine(LogLevel.Information, null, message, context);

    public void LogWarning(string message, object? context = null) => WriteLogLine(LogLevel.Warning, null, message, context);

    public void LogError(Exception exception, string message, object? context = null) => WriteLogLine(LogLevel.Error, exception, message, context);

    private void WriteLogLine(LogLevel logLevel, Exception? loggedException, string logMessage, object? logContext)
    {
        if (!frameworkLogger.IsEnabled(logLevel))
            return;

        using var logContextScope = logContext is null ? null : frameworkLogger.BeginScope(ReadLogContextFields(logContext));
        frameworkLogger.Log(logLevel, default, logMessage, loggedException, static (messageToWrite, _) => messageToWrite);
    }

    private static Dictionary<string, object?> ReadLogContextFields(object logContext) =>
        logContext.GetType().GetProperties()
            .Select(logContextProperty => (logContextProperty.Name, FieldValue: logContextProperty.GetValue(logContext)))
            .Where(logContextField => logContextField.FieldValue is not null)
            .ToDictionary(logContextField => logContextField.Name, logContextField => logContextField.FieldValue);
}
