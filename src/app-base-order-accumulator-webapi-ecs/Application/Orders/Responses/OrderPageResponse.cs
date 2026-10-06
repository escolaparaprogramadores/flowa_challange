namespace Base.OrderAccumulator.Application.Orders.Responses;

public sealed record OrderPageResponse(int Page, int PageSize, long Total, IReadOnlyList<ListedOrderResponse> Orders)
{
    public static OrderPageResponse MapFromStoredOrderPage(int pageNumber, int ordersPerPage, StoredOrderPageResponse storedOrderPage)
    {
        ArgumentNullException.ThrowIfNull(storedOrderPage);

        return new OrderPageResponse(
            pageNumber, ordersPerPage, storedOrderPage.TotalStoredOrders,
            storedOrderPage.StoredOrders.Select(ListedOrderResponse.MapFromStoredOrder).ToList());
    }
}
