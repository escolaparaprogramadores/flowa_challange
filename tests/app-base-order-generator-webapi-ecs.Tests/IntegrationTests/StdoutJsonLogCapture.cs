using System.Text.Json;

namespace Base.OrderGenerator.Tests;

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

// One line of the framework JSON console: the context fields and the TraceId come inside "Scopes".
public sealed record JsonLogLine(string LogLevel, string Category, string Message, IReadOnlyDictionary<string, string> ScopeFields, string? Exception)
{
    public string? TraceId => ScopeFields.GetValueOrDefault("TraceId");

    public string? ReadScopeField(string scopeFieldName) => ScopeFields.GetValueOrDefault(scopeFieldName);

    public static JsonLogLine ParseStdoutLine(string stdoutLine)
    {
        using var logLineDocument = JsonDocument.Parse(stdoutLine);
        var logLineJson = logLineDocument.RootElement;
        var scopeFields = new Dictionary<string, string>();
        if (logLineJson.TryGetProperty("Scopes", out var logScopes))
        {
            foreach (var logScope in logScopes.EnumerateArray().Where(logScope => logScope.ValueKind == JsonValueKind.Object))
            {
                foreach (var scopeField in logScope.EnumerateObject().Where(scopeField => scopeField.Name != "Message"))
                    scopeFields[scopeField.Name] = scopeField.Value.ToString();
            }
        }

        return new JsonLogLine(
            logLineJson.GetProperty("LogLevel").GetString()!,
            logLineJson.GetProperty("Category").GetString()!,
            logLineJson.GetProperty("Message").GetString()!,
            scopeFields,
            logLineJson.TryGetProperty("Exception", out var loggedException) ? loggedException.GetString() : null);
    }
}
