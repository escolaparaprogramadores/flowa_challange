using Base.OrderAccumulator.Domain.Orders;

namespace Base.OrderAccumulator.Infrastructure.Metrics;

// Tags with closed values: each new value becomes a paid series in Datadog.
// A symbol outside the list counts in a single series, so the total does not exceed 20 series.
public static class OrderMetricTagMapper
{
    public const string InvalidTagValue = "invalido";
    public const string BuyOrderSideTagValue = "buy";
    public const string SellOrderSideTagValue = "sell";

    public static string[] BuildOrderDecisionTags(string? orderSymbol, char orderSide)
    {
        if (orderSymbol is null || !OrderFieldRule.AllowedOrderSymbols.Contains(orderSymbol))
            return [$"symbol:{InvalidTagValue}", $"side:{InvalidTagValue}"];

        return [$"symbol:{orderSymbol}", $"side:{ToOrderSideTagValue(orderSide)}"];
    }

    public static string[] BuildSymbolExposureTags(string orderSymbol) => [$"symbol:{orderSymbol}"];

    private static string ToOrderSideTagValue(char orderSide) => orderSide switch
    {
        OrderSideCodes.BuyOrderSideFixCode => BuyOrderSideTagValue,
        OrderSideCodes.SellOrderSideFixCode => SellOrderSideTagValue,
        _ => InvalidTagValue
    };
}
