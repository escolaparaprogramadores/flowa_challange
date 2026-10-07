using Flowa.OrderAccumulator.Domain.Orders.Enums;

namespace Flowa.OrderAccumulator.Domain.Orders.ValueObjects;

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
