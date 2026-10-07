using Flowa.OrderGenerator.Domain.Orders.ValueObjects;

namespace Flowa.OrderGenerator.Application.Orders.Responses;

public sealed record OrderPageResponse(int Page, int PageSize, long Total, IReadOnlyList<ListedOrderResponse> Orders)
{
    public static OrderPageResponse MapFromStoredOrderPage(int orderListPageNumber, StoredOrderPageResponse storedOrderPage)
    {
        ArgumentNullException.ThrowIfNull(storedOrderPage);

        return new OrderPageResponse(
            orderListPageNumber, OrderListPagePolicy.OrdersPerPage, storedOrderPage.TotalStoredOrders,
            storedOrderPage.StoredOrders.Select(ListedOrderResponse.MapFromStoredOrder).ToList());
    }
}
