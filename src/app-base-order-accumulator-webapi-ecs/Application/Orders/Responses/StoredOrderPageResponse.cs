namespace Base.OrderAccumulator.Application.Orders.Responses;

public sealed record StoredOrderPageResponse(long TotalStoredOrders, IReadOnlyList<StoredOrderResponse> StoredOrders);
