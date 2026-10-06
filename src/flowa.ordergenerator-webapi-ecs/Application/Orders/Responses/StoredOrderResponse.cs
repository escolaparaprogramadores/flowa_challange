namespace Flowa.OrderGenerator.Application.Orders.Responses;

public sealed record StoredOrderResponse(
    DateTime ReceivedAt,
    bool Accepted,
    string? Symbol,
    string Side,
    decimal Quantity,
    decimal Price,
    string OrderId,
    string ClOrdId,
    string? RejectReason);
