using System.Diagnostics;

namespace Base.OrderGenerator.Infrastructure.Fix;

// Sending side of an order trace between the OrderGenerator and the OrderAccumulator. The W3C
// context (traceparent) travels in tag 5100 of the NewOrderSingle. With DD_TRACE_OTEL_ENABLED=true the
// Datadog tracer joins these spans to its own trace; with nobody listening to the source, no span is created.
public static class FixOrderTraceProvider
{
    public const int TraceParentTag = 5100;
    public const string TraceSourceName = "Flowa.Fix";

    private static readonly ActivitySource OrderTraceSource = new(TraceSourceName);

    public static Activity? StartOrderSending() =>
        OrderTraceSource.StartActivity("fix.envio_da_ordem", ActivityKind.Producer);

    public static string? GetTraceParentOfOrderSending(Activity? orderSending) =>
        orderSending is { IdFormat: ActivityIdFormat.W3C } ? orderSending.Id : null;

    // The order number is born from its trace (decision 21): the 32 hex of the 128-bit trace id, so the
    // ClOrdID is the id to search in the log and in the Datadog APM. The Datadog tracer gives this span the
    // trace id of the HTTP request span it starts inside. With no tracer there is no span: a random GUID.
    public static string CreateClOrdId(Activity? orderSending) =>
        orderSending is { IdFormat: ActivityIdFormat.W3C } ? orderSending.TraceId.ToString() : Guid.NewGuid().ToString("N");
}
