namespace Base.OrderAccumulator.Domain.Orders.ValueObjects;

public sealed record IncomingOrder(string ClOrdId, string? Symbol, char Side, decimal Quantity, decimal Price);
