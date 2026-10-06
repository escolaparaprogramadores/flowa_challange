namespace Base.OrderAccumulator.Application.Orders.ListOrders;

public sealed record OrderListPage(long TotalStoredOrders, IReadOnlyList<OrderListItem> StoredOrders);

public sealed record OrderListItem(
    DateTime ReceivedAt,
    bool Accepted,
    string? Symbol,
    string Side,
    decimal Quantity,
    decimal Price,
    string OrderId,
    string ClOrdId);
