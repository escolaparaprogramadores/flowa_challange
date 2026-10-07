using System.Diagnostics;

namespace Flowa.Commons.Observability;

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
