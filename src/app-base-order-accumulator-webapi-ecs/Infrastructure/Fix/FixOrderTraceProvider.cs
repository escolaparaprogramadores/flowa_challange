using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Base.OrderAccumulator.Infrastructure.Fix;

// Receiving side of an order trace between the OrderGenerator and the OrderAccumulator. The W3C
// context (traceparent) travels in tag 5100 of the NewOrderSingle. With DD_TRACE_OTEL_ENABLED=true the
// Datadog tracer joins these spans to its own trace; with nobody listening to the source, no span is created.
public static class FixOrderTraceProvider
{
    public const int TraceParentTag = 5100;
    public const string TraceSourceName = "Flowa.Fix";
    public const string HiddenLogValue = "***";

    private const string TraceParentTagPrefix = "5100=";
    private static readonly ActivitySource OrderTraceSource = new(TraceSourceName);
    private static readonly Regex TraceParentTagValuePattern = new("(?<=^|\u0001)5100=[^\u0001]*", RegexOptions.CultureInvariant);

    // A missing value or one outside the W3C format changes nothing in the order: receiving just opens a new trace.
    public static Activity? StartOrderReceiving(string? receivedTraceParent)
    {
        ActivityContext.TryParse(receivedTraceParent, null, out var orderSendingContext);
        return OrderTraceSource.StartActivity("fix.recebimento_da_ordem", ActivityKind.Consumer, orderSendingContext);
    }

    // The FIX session log writes the raw message, and the 5100 traceparent carries the trace id, which
    // stays out of the log (decision 17). The tag stays on the line, only its value goes away.
    public static string HideTraceParentInLog(string fixLogLine) =>
        fixLogLine.Contains(TraceParentTagPrefix, StringComparison.Ordinal)
            ? TraceParentTagValuePattern.Replace(fixLogLine, TraceParentTagPrefix + HiddenLogValue)
            : fixLogLine;
}
