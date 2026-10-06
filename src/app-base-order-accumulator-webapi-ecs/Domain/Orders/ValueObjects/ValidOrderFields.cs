namespace Base.OrderAccumulator.Domain.Orders;

// The four fields of an order that follow the field rule; only OrderFieldRule creates it.
public sealed record ValidOrderFields
{
    public string Symbol { get; }
    public OrderSide Side { get; }
    public int Quantity { get; }
    public decimal Price { get; }

    internal ValidOrderFields(string symbol, OrderSide side, int quantity, decimal price)
    {
        Symbol = symbol;
        Side = side;
        Quantity = quantity;
        Price = price;
    }
}

// Either the valid fields, or one reason per invalid field in the order symbol, side, quantity, price.
public sealed record OrderFieldValidation(ValidOrderFields? ValidOrderFields, IReadOnlyList<string> InvalidOrderFieldMessages);
