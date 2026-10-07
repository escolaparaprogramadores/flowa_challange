using Flowa.DatadogMetrics.Domain.Orders.Enums;
using Flowa.DatadogMetrics.Domain.Orders.ValueObjects;

namespace Flowa.DatadogMetrics.Infrastructure.Orders.Adapters;

internal static class OrderMetricTagMapper
{
    public const string InvalidTagValue = "invalido";
    public const string BuyOrderSideTagValue = "buy";
    public const string SellOrderSideTagValue = "sell";

    public static string BuildOrderDecisionTagKey(string? orderSymbol, string storedOrderSideFixCode)
    {
        if (!OrderSymbolPolicy.IsAllowedOrderSymbol(orderSymbol))
            return $"symbol:{InvalidTagValue},side:{InvalidTagValue}";

        return $"symbol:{orderSymbol},side:{ToOrderSideTagValue(storedOrderSideFixCode)}";
    }

    private static string ToOrderSideTagValue(string storedOrderSideFixCode) => OrderSideCodes.ConvertStoredFixCodeToOrderSide(storedOrderSideFixCode) switch
    {
        OrderSide.Buy => BuyOrderSideTagValue,
        OrderSide.Sell => SellOrderSideTagValue,
        _ => InvalidTagValue
    };
}
