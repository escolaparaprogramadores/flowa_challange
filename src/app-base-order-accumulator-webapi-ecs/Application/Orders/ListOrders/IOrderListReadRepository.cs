namespace Base.OrderAccumulator.Application.Orders.ListOrders;

public interface IOrderListReadRepository
{
    Task<OrderListPage> ReadStoredOrderPageAsync(int pageNumber, CancellationToken cancellationToken = default);
}
