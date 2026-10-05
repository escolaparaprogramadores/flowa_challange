namespace Base.OrderAccumulator.Domain.Orders;

// The order as it arrived by FIX, before any validation.
public sealed record IncomingOrder(string ClOrdId, string? Symbol, char Side, decimal Quantity, decimal Price);
