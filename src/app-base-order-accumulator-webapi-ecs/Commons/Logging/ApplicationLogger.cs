using System.Collections;
using Base.OrderAccumulator.Commons;

namespace Base.OrderAccumulator.Infrastructure.Logging;

// Writes through the framework logger, which prints one JSON line per event. The context goes in as the
// structured state of the event, so each field becomes a JSON field of "State"; fields without a value are left out.
public sealed class ApplicationLogger<T>(ILogger<T> frameworkLogger) : IApplicationLogger<T>
{
    public void LogInformation(string message, object? context = null) => WriteLogLine(LogLevel.Information, null, message, context);

    public void LogWarning(string message, object? context = null) => WriteLogLine(LogLevel.Warning, null, message, context);

    public void LogError(Exception exception, string message, object? context = null) => WriteLogLine(LogLevel.Error, exception, message, context);

    private void WriteLogLine(LogLevel logLevel, Exception? loggedException, string logMessage, object? logContext)
    {
        if (!frameworkLogger.IsEnabled(logLevel))
            return;

        frameworkLogger.Log(logLevel, default, new LogLineState(logMessage, ReadLogContextFields(logContext)), loggedException,
            static (logLineState, _) => logLineState.ToString());
    }

    private static List<KeyValuePair<string, object?>> ReadLogContextFields(object? logContext) =>
        logContext is null
            ? []
            : logContext.GetType().GetProperties()
                .Select(logContextProperty => new KeyValuePair<string, object?>(logContextProperty.Name, logContextProperty.GetValue(logContext)))
                .Where(logContextField => logContextField.Value is not null)
                .ToList();

    // The shape the framework reads as structured state: the fields, and the message as ToString.
    private sealed class LogLineState(string logMessage, List<KeyValuePair<string, object?>> logContextFields) : IReadOnlyList<KeyValuePair<string, object?>>
    {
        public KeyValuePair<string, object?> this[int logContextFieldIndex] => logContextFields[logContextFieldIndex];

        public int Count => logContextFields.Count;

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => logContextFields.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => logMessage;
    }
}
