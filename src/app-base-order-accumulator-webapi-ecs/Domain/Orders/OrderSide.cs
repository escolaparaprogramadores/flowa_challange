namespace Base.OrderAccumulator.Domain.Orders;

public enum OrderSide
{
    Buy,
    Sell
}

// Tag 54 values of the FIX message and the "side" values of the HTTP contract.
public static class OrderSideCodes
{
    public const string BuyOrderSideJsonCode = "buy";
    public const string SellOrderSideJsonCode = "sell";
    public const char BuyOrderSideFixCode = '1';
    public const char SellOrderSideFixCode = '2';
}
