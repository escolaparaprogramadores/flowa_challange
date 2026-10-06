namespace Base.OrderAccumulator.Domain.Orders.Enums;

public static class OrderSideCodes
{
    public const char BuyOrderSideFixCode = '1';
    public const char SellOrderSideFixCode = '2';

    public static OrderSide? ConvertFixCodeToOrderSide(char orderSideFixCode) => orderSideFixCode switch
    {
        BuyOrderSideFixCode => OrderSide.Buy,
        SellOrderSideFixCode => OrderSide.Sell,
        _ => null
    };

    public static OrderSide? ConvertStoredFixCodeToOrderSide(string storedOrderSideFixCode) => storedOrderSideFixCode switch
    {
        [var orderSideFixCode] => ConvertFixCodeToOrderSide(orderSideFixCode),
        _ => null
    };
}
