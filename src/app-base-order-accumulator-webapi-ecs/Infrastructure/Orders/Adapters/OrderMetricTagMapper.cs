using Base.OrderAccumulator.Domain.Orders.Enums;
using Base.OrderAccumulator.Domain.Orders.ValueObjects;

namespace Base.OrderAccumulator.Infrastructure.Orders.Adapters;

public static class OrderMetricTagMapper
{
    public const string InvalidTagValue = "invalido";
    public const string BuyOrderSideTagValue = "buy";
    public const string SellOrderSideTagValue = "sell";

    public static string[] BuildOrderDecisionTags(string? orderSymbol, char orderSide)
    {
        if (orderSymbol is null || !OrderFieldPolicy.AllowedOrderSymbols.Contains(orderSymbol))
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
