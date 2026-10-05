using System.Diagnostics;

namespace Base.OrderGenerator.Infrastructure.Tracing;

// The OrderGenerator is the public edge: a traceparent sent by the caller is not continued, so every request
// starts its own trace. The ClOrdID is the trace id of the order (decision 21); continuing the caller's trace
// would give two orders of the same caller trace the same ClOrdID, and the second would be answered as a
// repeat. Outgoing calls still carry the context. The Datadog tracer has the same rule in the Dockerfile
// (DD_TRACE_PROPAGATION_STYLE_EXTRACT=none).
public sealed class IncomingTraceContextIgnoringPropagator : DistributedContextPropagator
{
    private static readonly DistributedContextPropagator OutgoingTraceContextPropagator = CreateDefaultPropagator();

    public override IReadOnlyCollection<string> Fields => OutgoingTraceContextPropagator.Fields;

    public override void Inject(Activity? activity, object? carrier, PropagatorSetterCallback? setter) =>
        OutgoingTraceContextPropagator.Inject(activity, carrier, setter);

    public override void ExtractTraceIdAndState(object? carrier, PropagatorGetterCallback? getter, out string? traceId, out string? traceState)
    {
        traceId = null;
        traceState = null;
    }

    public override IEnumerable<KeyValuePair<string, string?>>? ExtractBaggage(object? carrier, PropagatorGetterCallback? getter) => null;
}
