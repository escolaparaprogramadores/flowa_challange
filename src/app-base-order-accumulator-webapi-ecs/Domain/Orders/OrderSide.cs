namespace Base.OrderAccumulator.Domain.Orders;

public enum OrderSide
{
    Buy,
    Sell
}

// Tag 54 values of the FIX message, the language the two contexts share.
public static class OrderSideCodes
{
    public const char BuyOrderSideFixCode = '1';
    public const char SellOrderSideFixCode = '2';
}
