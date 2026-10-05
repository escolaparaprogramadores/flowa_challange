using System.Diagnostics;

namespace Base.OrderAccumulator.Infrastructure.Fix;

// Receiving side of an order trace between the OrderGenerator and the OrderAccumulator. The W3C
// context (traceparent) travels in tag 5100 of the NewOrderSingle. With DD_TRACE_OTEL_ENABLED=true the
// Datadog tracer joins these spans to its own trace; with nobody listening to the source, no span is created.
public static class FixOrderTraceProvider
{
    public const int TraceParentTag = 5100;
    public const string TraceSourceName = "Flowa.Fix";

    private static readonly ActivitySource OrderTraceSource = new(TraceSourceName);

    // A missing value or one outside the W3C format changes nothing in the order: receiving just opens a new trace.
    public static Activity? StartOrderReceiving(string? receivedTraceParent)
    {
        ActivityContext.TryParse(receivedTraceParent, null, out var orderSendingContext);
        return OrderTraceSource.StartActivity("fix.recebimento_da_ordem", ActivityKind.Consumer, orderSendingContext);
    }
}
