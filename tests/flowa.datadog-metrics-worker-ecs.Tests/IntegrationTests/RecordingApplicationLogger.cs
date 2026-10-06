using System.Collections.Concurrent;
using Flowa.Commons.Logging;

namespace Flowa.DatadogMetrics.Tests;

// Records each line as "<level> <message>", so the test compares the exact list a class wrote.
public sealed class RecordingApplicationLogger<T> : IApplicationLogger<T>
{
    private readonly ConcurrentQueue<string> recordedLogLines = new();

    public IReadOnlyList<string> RecordedLogLines => recordedLogLines.ToList();

    public void LogInformation(string message, object? context = null) => recordedLogLines.Enqueue($"Information {message}");

    public void LogWarning(string message, object? context = null) => recordedLogLines.Enqueue($"Warning {message}");

    public void LogError(Exception exception, string message, object? context = null) => recordedLogLines.Enqueue($"Error {message}");

    public async Task WaitForLogLineCountAsync(int expectedLogLineCount)
    {
        using var logLineWait = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (recordedLogLines.Count < expectedLogLineCount)
            await Task.Delay(TimeSpan.FromMilliseconds(50), logLineWait.Token);
    }
}
