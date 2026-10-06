using Flowa.OrderAccumulator.Domain.Orders.Entities;

namespace Flowa.OrderAccumulator.Domain.Orders.Interfaces;

public interface IOrderRepository
{
    Task<Order?> FindOrderByClOrdIdAsync(string clOrdId, CancellationToken cancellationToken = default);

    Task<bool> TryAddOrderAsync(Order answeredOrder, CancellationToken cancellationToken = default);
}
