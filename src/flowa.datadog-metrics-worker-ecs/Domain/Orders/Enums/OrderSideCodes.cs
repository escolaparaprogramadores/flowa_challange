namespace Flowa.DatadogMetrics.Domain.Orders.Enums;

public static class OrderSideCodes
{
    public const string BuyOrderSideFixCode = "1";
    public const string SellOrderSideFixCode = "2";

    public static OrderSide? ConvertStoredFixCodeToOrderSide(string storedOrderSideFixCode) => storedOrderSideFixCode switch
    {
        BuyOrderSideFixCode => OrderSide.Buy,
        SellOrderSideFixCode => OrderSide.Sell,
        _ => null
    };
}
