using System.Diagnostics;

namespace Flowa.OrderAccumulator.Infrastructure.Fix;

public static class FixOrderTraceProvider
{
    public const int TraceParentTag = 5100;
    public const string TraceSourceName = "Flowa.Fix";

    private static readonly ActivitySource OrderTraceSource = new(TraceSourceName);

    public static Activity? StartOrderReceiving(string? receivedTraceParent)
    {
        ActivityContext.TryParse(receivedTraceParent, null, out var orderSendingContext);
        return OrderTraceSource.StartActivity("fix.recebimento_da_ordem", ActivityKind.Consumer, orderSendingContext);
    }
}
