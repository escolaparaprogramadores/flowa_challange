using System.Diagnostics;

namespace Flowa.OrderGenerator.Infrastructure.Fix;

internal static class FixOrderTraceProvider
{
    public const int TraceParentTag = 5100;
    public const string TraceSourceName = "Flowa.Fix";

    private static readonly ActivitySource OrderTraceSource = new(TraceSourceName);

    public static Activity? StartOrderSending() =>
        OrderTraceSource.StartActivity("fix.envio_da_ordem", ActivityKind.Producer);

    public static string? GetTraceParentOfOrderSending(Activity? orderSending) =>
        orderSending is { IdFormat: ActivityIdFormat.W3C } ? orderSending.Id : null;

    public static string CreateClOrdId(Activity? orderSending) =>
        orderSending is { IdFormat: ActivityIdFormat.W3C } ? orderSending.TraceId.ToString() : Guid.NewGuid().ToString("N");
}
