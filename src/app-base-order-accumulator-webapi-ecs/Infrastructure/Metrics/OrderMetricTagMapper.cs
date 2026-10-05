using Flowa.Shared;

namespace Base.OrderAccumulator.Infrastructure.Metrics;

// Etiquetas com valores fechados: cada valor novo vira uma série paga no Datadog.
// Símbolo fora da lista conta numa série só, para o total não passar de 20 séries.
public static class OrderMetricTagMapper
{
    public const string InvalidTagValue = "invalido";

    public static string[] BuildOrderDecisionTags(string? orderSymbol, char orderSide)
    {
        if (orderSymbol is null || !OrderRules.AllowedOrderSymbols.Contains(orderSymbol))
            return [$"symbol:{InvalidTagValue}", $"side:{InvalidTagValue}"];

        return [$"symbol:{orderSymbol}", $"side:{ToOrderSideTagValue(orderSide)}"];
    }

    public static string[] BuildSymbolExposureTags(string orderSymbol) => [$"symbol:{orderSymbol}"];

    private static string ToOrderSideTagValue(char orderSide) => orderSide switch
    {
        OrderSideCodes.BuyOrderSideFixCode => OrderSideCodes.BuyOrderSideJsonCode,
        OrderSideCodes.SellOrderSideFixCode => OrderSideCodes.SellOrderSideJsonCode,
        _ => InvalidTagValue
    };
}
