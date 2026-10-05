using System.Diagnostics;
using Base.OrderGenerator.Commons;
using Base.OrderGenerator.Infrastructure.Fix;

namespace Base.OrderGenerator.Tests;

// The FIX session log as JSON fields: SOH shown as "|", heartbeats left out (decision 22), the whole
// traceparent kept (decision 17 fell) and, outside a span, the ClOrdID as the trace id (decision 21).
public class FixSessionLogTests
{
    private const string OrderTraceId = "0af7651916cd43dd8448eb211c80319c";
    private const string OrderTraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
    private const string FixSessionName = "FIX.4.4:ORDERGENERATOR->ORDERACCUMULATOR";

    [Fact]
    public void Received_order_is_one_information_line_with_the_message_the_whole_traceparent_and_the_clordid_as_trace_id()
    {
        var recordingLogger = new RecordingFixSessionLogger();
        var fixSessionLog = new FixSessionLog(recordingLogger, FixSessionName);

        fixSessionLog.OnIncoming($"8=FIX.4.4\u000135=D\u000111={OrderTraceId}\u00015100={OrderTraceParent}\u000110=128\u0001");

        var recordedFixLine = Assert.Single(recordingLogger.RecordedLogLines);
        Assert.Equal("Information", recordedFixLine.LogLevel);
        Assert.Equal("FIX message received.", recordedFixLine.Message);
        Assert.Equal($"8=FIX.4.4|35=D|11={OrderTraceId}|5100={OrderTraceParent}|10=128|", recordedFixLine.ContextFields["FixMessage"]);
        Assert.Equal(FixSessionName, recordedFixLine.ContextFields["FixSession"]);
        Assert.Equal(OrderTraceId, recordedFixLine.ContextFields["TraceId"]);
    }

    [Fact]
    public void Sent_message_is_logged_with_its_own_text()
    {
        var recordingLogger = new RecordingFixSessionLogger();

        new FixSessionLog(recordingLogger, FixSessionName).OnOutgoing("8=FIX.4.4\u000135=A\u000149=ORDERGENERATOR\u000110=200\u0001");

        var recordedFixLine = Assert.Single(recordingLogger.RecordedLogLines);
        Assert.Equal("FIX message sent.", recordedFixLine.Message);
        Assert.Equal("8=FIX.4.4|35=A|49=ORDERGENERATOR|10=200|", recordedFixLine.ContextFields["FixMessage"]);
        Assert.Null(recordedFixLine.ContextFields["TraceId"]);
    }

    [Theory]
    [InlineData("8=FIX.4.4\u00019=60\u000135=0\u000134=7\u000110=111\u0001")]
    [InlineData("8=FIX.4.4\u000135=0\u000110=111\u0001")]
    public void Heartbeat_in_or_out_is_not_logged(string heartbeatFixMessage)
    {
        var recordingLogger = new RecordingFixSessionLogger();
        var fixSessionLog = new FixSessionLog(recordingLogger, FixSessionName);

        fixSessionLog.OnIncoming(heartbeatFixMessage);
        fixSessionLog.OnOutgoing(heartbeatFixMessage);

        Assert.Empty(recordingLogger.RecordedLogLines);
    }

    [Fact]
    public void Message_type_ending_in_zero_that_is_not_a_heartbeat_is_logged()
    {
        var recordingLogger = new RecordingFixSessionLogger();

        new FixSessionLog(recordingLogger, FixSessionName).OnIncoming("8=FIX.4.4\u000135=AE0\u0001135=0\u000110=111\u0001");

        Assert.Single(recordingLogger.RecordedLogLines);
    }

    [Theory]
    [InlineData("log-ca19")]
    [InlineData("0AF7651916CD43DD8448EB211C80319C")]
    [InlineData("0af7651916cd43dd8448eb211c80319")]
    public void ClOrdId_that_is_not_32_lowercase_hex_gives_no_trace_id(string clOrdIdThatIsNotATraceId)
    {
        var recordingLogger = new RecordingFixSessionLogger();

        new FixSessionLog(recordingLogger, FixSessionName).OnIncoming($"8=FIX.4.4\u000135=8\u000111={clOrdIdThatIsNotATraceId}\u000110=128\u0001");

        Assert.Null(Assert.Single(recordingLogger.RecordedLogLines).ContextFields["TraceId"]);
    }

    [Fact]
    public void Inside_a_span_the_adapter_adds_no_trace_id_because_the_framework_puts_the_span_one()
    {
        var recordingLogger = new RecordingFixSessionLogger();
        using var orderSpan = new Activity("fix.recebimento_da_ordem").Start();

        new FixSessionLog(recordingLogger, FixSessionName).OnOutgoing($"8=FIX.4.4\u000135=8\u000111={OrderTraceId}\u000110=128\u0001");

        Assert.Null(Assert.Single(recordingLogger.RecordedLogLines).ContextFields["TraceId"]);
    }

    [Fact]
    public void Session_event_is_an_information_line_with_the_session_name()
    {
        var recordingLogger = new RecordingFixSessionLogger();

        new FixSessionLog(recordingLogger, FixSessionName).OnEvent("Session reset: ResetOnLogon");

        var recordedEventLine = Assert.Single(recordingLogger.RecordedLogLines);
        Assert.Equal(("Information", "Session reset: ResetOnLogon"), (recordedEventLine.LogLevel, recordedEventLine.Message));
        Assert.Equal(FixSessionName, recordedEventLine.ContextFields["FixSession"]);
    }

    private sealed record RecordedLogLine(string LogLevel, string Message, IReadOnlyDictionary<string, object?> ContextFields);

    private sealed class RecordingFixSessionLogger : IApplicationLogger<FixSessionLog>
    {
        public List<RecordedLogLine> RecordedLogLines { get; } = [];

        public void LogInformation(string message, object? context = null) => RecordedLogLines.Add(new("Information", message, ReadContextFields(context)));

        public void LogWarning(string message, object? context = null) => RecordedLogLines.Add(new("Warning", message, ReadContextFields(context)));

        public void LogError(Exception exception, string message, object? context = null) => RecordedLogLines.Add(new("Error", message, ReadContextFields(context)));

        private static IReadOnlyDictionary<string, object?> ReadContextFields(object? context) =>
            context?.GetType().GetProperties().ToDictionary(contextProperty => contextProperty.Name, contextProperty => contextProperty.GetValue(context))
            ?? new Dictionary<string, object?>();
    }
}
