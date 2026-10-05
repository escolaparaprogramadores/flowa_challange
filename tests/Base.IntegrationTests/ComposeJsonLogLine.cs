using System.Text.Json;

namespace Base.IntegrationTests;

// One line of a container log, as docker compose logs prints it: the framework JSON console of the app.
// The context fields of the application come in "State"; the TraceId of the current span comes in "Scopes".
// A line that is not JSON fails the parse (CA-7).
public sealed record ComposeJsonLogLine(string LogLevel, string Message, IReadOnlyDictionary<string, string> LogFields)
{
    public string? TraceId => LogFields.GetValueOrDefault("TraceId");

    public string? ReadLogField(string logFieldName) => LogFields.GetValueOrDefault(logFieldName);

    public static ComposeJsonLogLine ParseContainerLogLine(string containerLogLine)
    {
        using var logLineDocument = JsonDocument.Parse(containerLogLine);
        var logLineJson = logLineDocument.RootElement;
        var logFields = new Dictionary<string, string>();
        if (logLineJson.TryGetProperty("State", out var logState))
            ReadLogFieldsOf(logState, logFields);
        if (logLineJson.TryGetProperty("Scopes", out var logScopes))
        {
            foreach (var logScope in logScopes.EnumerateArray().Where(logScope => logScope.ValueKind == JsonValueKind.Object))
                ReadLogFieldsOf(logScope, logFields);
        }

        return new ComposeJsonLogLine(logLineJson.GetProperty("LogLevel").GetString()!, logLineJson.GetProperty("Message").GetString()!, logFields);
    }

    private static void ReadLogFieldsOf(JsonElement logFieldsObject, Dictionary<string, string> logFields)
    {
        foreach (var logField in logFieldsObject.EnumerateObject().Where(logField => logField.Name is not ("Message" or "{OriginalFormat}")))
            logFields[logField.Name] = logField.Value.ToString();
    }
}
