namespace Base.OrderGenerator.Domain.Orders;

public enum OrderSide
{
    Buy,
    Sell
}

// An order in the shape a FIX NewOrderSingle carries. The field rule is not checked here: the
// OrderAccumulator owns it and answers through FIX whether the order is valid (decision 12).
public sealed record OrderToSend(string Symbol, OrderSide Side, decimal Quantity, decimal Price);
