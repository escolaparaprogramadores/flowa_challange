namespace Base.OrderAccumulator.Application.Orders.ListOrders;

// The order list of the screen, newest first, one page at a time.
public sealed class ListOrdersUseCase(IOrderListReadRepository orderListReadRepository)
{
    public Task<OrderListPage> ListOrdersAsync(int pageNumber, CancellationToken cancellationToken = default) =>
        orderListReadRepository.ReadStoredOrderPageAsync(pageNumber, cancellationToken);
}
