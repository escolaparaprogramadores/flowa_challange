using System.Collections.Concurrent;
using System.Text.Json;
using Flowa.Commons.Logging;

namespace Flowa.DatadogMetrics.Tests;

// Records each line as "<level> <message>", so the test compares the exact list a class wrote, and the context of
// each Information line as JSON, so the test reads the values the line carried.
public sealed class RecordingApplicationLogger<T> : IApplicationLogger<T>
{
    private readonly ConcurrentQueue<string> recordedLogLines = new();
    private readonly ConcurrentQueue<JsonElement> recordedInformationContexts = new();

    public IReadOnlyList<string> RecordedLogLines => recordedLogLines.ToList();

    public IReadOnlyList<JsonElement> RecordedInformationContexts => recordedInformationContexts.ToList();

    public void LogInformation(string message, object? context = null)
    {
        recordedInformationContexts.Enqueue(JsonSerializer.SerializeToElement(context));
        recordedLogLines.Enqueue($"Information {message}");
    }

    public void LogWarning(string message, object? context = null) => recordedLogLines.Enqueue($"Warning {message}");

    public void LogError(Exception exception, string message, object? context = null) => recordedLogLines.Enqueue($"Error {message}");

    public async Task WaitForLogLineCountAsync(int expectedLogLineCount)
    {
        using var logLineWait = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (recordedLogLines.Count < expectedLogLineCount)
            await Task.Delay(TimeSpan.FromMilliseconds(50), logLineWait.Token);
    }
}
