using Base.OrderAccumulator.Commons.Logging;
using Microsoft.Extensions.Logging;

namespace Base.OrderAccumulator.Tests;

// The implementation of IApplicationLogger<T>: the context becomes the structured state of the event (one JSON
// field each in "State"), fields without a value stay out, and the level and the exception go as asked.
public class ApplicationLoggerTests
{
    [Fact]
    public void Context_fields_become_state_fields_and_the_ones_without_value_are_left_out()
    {
        var recordingFrameworkLogger = new RecordingFrameworkLogger();

        new ApplicationLogger<ApplicationLoggerTests>(recordingFrameworkLogger)
            .LogWarning("Order rejected: invalid fields.", new { ErrorCode = "invalid_order_fields", TraceId = (string?)null, FixSession = (string?)null });

        var recordedLogEvent = Assert.Single(recordingFrameworkLogger.RecordedLogEvents);
        Assert.Equal(LogLevel.Warning, recordedLogEvent.LogLevel);
        Assert.Equal("Order rejected: invalid fields.", recordedLogEvent.FormattedMessage);
        Assert.Equal("Order rejected: invalid fields.", recordedLogEvent.StateText);
        Assert.Equal([new KeyValuePair<string, object?>("ErrorCode", "invalid_order_fields")], recordedLogEvent.StateFields);
        Assert.Null(recordedLogEvent.LoggedException);
    }

    [Fact]
    public void Error_carries_the_whole_exception_and_its_context()
    {
        var recordingFrameworkLogger = new RecordingFrameworkLogger();
        var unexpectedFailure = new InvalidOperationException("database down");

        new ApplicationLogger<ApplicationLoggerTests>(recordingFrameworkLogger)
            .LogError(unexpectedFailure, "Order decision failed; no ExecutionReport sent.", new { ErrorCode = "error" });

        var recordedLogEvent = Assert.Single(recordingFrameworkLogger.RecordedLogEvents);
        Assert.Equal(LogLevel.Error, recordedLogEvent.LogLevel);
        Assert.Same(unexpectedFailure, recordedLogEvent.LoggedException);
        Assert.Equal([new KeyValuePair<string, object?>("ErrorCode", "error")], recordedLogEvent.StateFields);
    }

    [Fact]
    public void Information_without_context_has_the_message_and_no_state_field()
    {
        var recordingFrameworkLogger = new RecordingFrameworkLogger();

        new ApplicationLogger<ApplicationLoggerTests>(recordingFrameworkLogger).LogInformation("Repeated ClOrdID: sending the stored answer back.");

        var recordedLogEvent = Assert.Single(recordingFrameworkLogger.RecordedLogEvents);
        Assert.Equal((LogLevel.Information, "Repeated ClOrdID: sending the stored answer back."), (recordedLogEvent.LogLevel, recordedLogEvent.FormattedMessage));
        Assert.Empty(recordedLogEvent.StateFields);
    }

    [Fact]
    public void Disabled_level_writes_nothing()
    {
        var recordingFrameworkLogger = new RecordingFrameworkLogger(minimumEnabledLevel: LogLevel.Warning);

        new ApplicationLogger<ApplicationLoggerTests>(recordingFrameworkLogger).LogInformation("FIX message sent.", new { FixMessage = "8=FIX.4.4|35=A|" });

        Assert.Empty(recordingFrameworkLogger.RecordedLogEvents);
    }

    private sealed record RecordedLogEvent(
        LogLevel LogLevel, string FormattedMessage, string? StateText, IReadOnlyList<KeyValuePair<string, object?>> StateFields, Exception? LoggedException);

    private sealed class RecordingFrameworkLogger(LogLevel minimumEnabledLevel = LogLevel.Information) : ILogger<ApplicationLoggerTests>
    {
        public List<RecordedLogEvent> RecordedLogEvents { get; } = [];

        public IDisposable? BeginScope<TLogScopeState>(TLogScopeState logScopeState) where TLogScopeState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimumEnabledLevel;

        public void Log<TLogEntryState>(LogLevel logLevel, EventId logEventId, TLogEntryState logEntryState, Exception? loggedException,
            Func<TLogEntryState, Exception?, string> logMessageFormatter) =>
            RecordedLogEvents.Add(new RecordedLogEvent(
                logLevel,
                logMessageFormatter(logEntryState, loggedException),
                logEntryState?.ToString(),
                logEntryState as IReadOnlyList<KeyValuePair<string, object?>> ?? [],
                loggedException));
    }
}
