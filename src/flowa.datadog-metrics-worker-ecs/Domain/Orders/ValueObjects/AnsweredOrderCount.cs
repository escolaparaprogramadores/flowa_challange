namespace Flowa.DatadogMetrics.Domain.Orders.ValueObjects;

public sealed record AnsweredOrderCount
{
    public string? Symbol { get; }
    public string Side { get; }
    public bool Accepted { get; }
    public long OrderCount { get; }
    public long LastOrderId { get; }

    public AnsweredOrderCount(string? symbol, string side, bool accepted, long orderCount, long lastOrderId)
    {
        ArgumentNullException.ThrowIfNull(side);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(orderCount);

        Symbol = symbol;
        Side = side;
        Accepted = accepted;
        OrderCount = orderCount;
        LastOrderId = lastOrderId;
    }
}
