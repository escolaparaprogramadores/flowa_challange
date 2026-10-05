namespace Base.OrderAccumulator.Domain.Orders;

public interface IOrderRepository
{
    Task<Order?> FindOrderByClOrdIdAsync(string clOrdId, CancellationToken cancellationToken = default);

    // False when an order with the same ClOrdID is already stored; nothing is written then.
    Task<bool> TryAddOrderAsync(Order answeredOrder, CancellationToken cancellationToken = default);

    Task DeleteAllOrdersAsync(CancellationToken cancellationToken = default);
}
