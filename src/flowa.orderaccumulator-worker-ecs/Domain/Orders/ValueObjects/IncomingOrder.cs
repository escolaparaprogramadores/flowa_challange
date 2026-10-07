namespace Flowa.OrderAccumulator.Domain.Orders.ValueObjects;

public sealed record IncomingOrder
{
    public string ClOrdId { get; }
    public string? Symbol { get; }
    public char Side { get; }
    public decimal Quantity { get; }
    public decimal Price { get; }

    public IncomingOrder(string clOrdId, string? symbol, char side, decimal quantity, decimal price)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clOrdId);

        ClOrdId = clOrdId;
        Symbol = symbol;
        Side = side;
        Quantity = quantity;
        Price = price;
    }
}
