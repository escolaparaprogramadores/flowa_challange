using System.Collections;

namespace Base.OrderGenerator.Commons.Logging;

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

    private sealed class LogLineState(string logMessage, List<KeyValuePair<string, object?>> logContextFields) : IReadOnlyList<KeyValuePair<string, object?>>
    {
        public KeyValuePair<string, object?> this[int logContextFieldIndex] => logContextFields[logContextFieldIndex];

        public int Count => logContextFields.Count;

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => logContextFields.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => logMessage;
    }
}
