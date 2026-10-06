namespace Base.OrderGenerator.Application.Orders.Responses;

public sealed record SentOrderResponse(
    string Status, string ClOrdId, string? OrderId, string? ExecId, string Symbol, string Side, decimal Quantity, decimal Price);
