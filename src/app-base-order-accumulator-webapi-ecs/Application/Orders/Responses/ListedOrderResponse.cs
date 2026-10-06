namespace Base.OrderAccumulator.Application.Orders.Responses;

public sealed record ListedOrderResponse(
    DateTime ReceivedAt, string Status, string? Symbol, string? Side, decimal Quantity, decimal Price, string OrderId, string ClOrdId);
