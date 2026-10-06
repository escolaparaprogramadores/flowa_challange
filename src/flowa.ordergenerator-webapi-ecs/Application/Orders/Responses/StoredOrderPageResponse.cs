namespace Flowa.OrderGenerator.Application.Orders.Responses;

public sealed record StoredOrderPageResponse(long TotalStoredOrders, IReadOnlyList<StoredOrderResponse> StoredOrders)
{
    public static StoredOrderPageResponse CreateEmptyStoredOrderPage() => new(0, []);
}
