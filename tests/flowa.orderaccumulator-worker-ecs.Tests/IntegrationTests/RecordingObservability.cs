using System.Collections.Concurrent;
using Flowa.Commons.Logging;
using Flowa.Commons.Observability;

namespace Flowa.OrderAccumulator.Tests;

// Records each line a use case writes, as "<level> <message>", so the test compares the exact list.
public sealed class RecordingApplicationLogger<T> : IApplicationLogger<T>
{
    private readonly ConcurrentQueue<string> recordedLogLines = new();

    public IReadOnlyList<string> RecordedLogLines => recordedLogLines.ToList();

    public void LogInformation(string message, object? context = null) => recordedLogLines.Enqueue($"Information {message}");

    public void LogWarning(string message, object? context = null) => recordedLogLines.Enqueue($"Warning {message}");

    public void LogError(Exception exception, string message, object? context = null) => recordedLogLines.Enqueue($"Error {message}");
}

// Records the name each use case opens and the result it closes with, in the order the operations end.
public sealed class RecordingOperationMonitoring : IOperationMonitoring
{
    private readonly ConcurrentQueue<(string OperationName, string OperationResult)> recordedOperations = new();

    public IReadOnlyList<(string OperationName, string OperationResult)> RecordedOperations => recordedOperations.ToList();

    public IMonitoredOperation StartOperationMonitoring(string operationName) => new RecordedOperation(operationName, recordedOperations);

    private sealed class RecordedOperation(string operationName, ConcurrentQueue<(string, string)> recordedOperations) : IMonitoredOperation
    {
        private string operationResult = OperationResults.Succeeded;

        public void RecordOperationResult(string operationResult) => this.operationResult = operationResult;

        public void Dispose() => recordedOperations.Enqueue((operationName, operationResult));
    }
}
