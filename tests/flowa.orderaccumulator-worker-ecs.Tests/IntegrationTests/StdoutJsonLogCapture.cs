using System.Text.Json;

namespace Flowa.OrderAccumulator.Tests;

// Reads the real stdout of the app, what docker compose logs and CloudWatch receive. The console logger
// keeps the stdout it found when the host was built, so start the capture before building the host and
// read the lines after disposing it (that is when the console logger has written everything).
public sealed class StdoutJsonLogCapture : IDisposable
{
    private readonly TextWriter _originalStdout = Console.Out;
    private readonly StringWriter _capturedStdout = new();

    public StdoutJsonLogCapture() => Console.SetOut(TextWriter.Synchronized(_capturedStdout));

    public IReadOnlyList<string> StdoutLines =>
        _capturedStdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // Every stdout line has to be one JSON log line: a line that is not JSON fails the parse here.
    public IReadOnlyList<JsonLogLine> JsonLogLines => StdoutLines.Select(JsonLogLine.ParseStdoutLine).ToList();

    public void Dispose() => Console.SetOut(_originalStdout);
}

// One line of the framework JSON console. The context fields of the application come in "State"; the
// TraceId and SpanId of the current span come in "Scopes".
public sealed record JsonLogLine(string LogLevel, string Category, string Message, IReadOnlyDictionary<string, string> LogFields, string? Exception)
{
    public string? TraceId => LogFields.GetValueOrDefault("TraceId");

    public string? ReadLogField(string logFieldName) => LogFields.GetValueOrDefault(logFieldName);

    public static JsonLogLine ParseStdoutLine(string stdoutLine)
    {
        using var logLineDocument = JsonDocument.Parse(stdoutLine);
        var logLineJson = logLineDocument.RootElement;
        var logFields = new Dictionary<string, string>();
        if (logLineJson.TryGetProperty("State", out var logState))
            ReadLogFieldsOf(logState, logFields);
        if (logLineJson.TryGetProperty("Scopes", out var logScopes))
        {
            foreach (var logScope in logScopes.EnumerateArray().Where(logScope => logScope.ValueKind == JsonValueKind.Object))
                ReadLogFieldsOf(logScope, logFields);
        }

        return new JsonLogLine(
            logLineJson.GetProperty("LogLevel").GetString()!,
            logLineJson.GetProperty("Category").GetString()!,
            logLineJson.GetProperty("Message").GetString()!,
            logFields,
            logLineJson.TryGetProperty("Exception", out var loggedException) ? loggedException.GetString() : null);
    }

    private static void ReadLogFieldsOf(JsonElement logFieldsObject, Dictionary<string, string> logFields)
    {
        foreach (var logField in logFieldsObject.EnumerateObject().Where(logField => logField.Name is not ("Message" or "{OriginalFormat}")))
            logFields[logField.Name] = logField.Value.ToString();
    }
}
