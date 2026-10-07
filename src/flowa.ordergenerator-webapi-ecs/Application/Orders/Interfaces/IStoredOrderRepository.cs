using Flowa.OrderGenerator.Application.Orders.Responses;

namespace Flowa.OrderGenerator.Application.Orders.Interfaces;

public interface IStoredOrderRepository
{
    Task<bool> OrderTableExistsAsync(CancellationToken cancellationToken);

    Task<StoredOrderPageResponse> ReadStoredOrderPageAsync(int orderListPageNumber, CancellationToken cancellationToken);

    Task DeleteAllStoredOrdersAsync(CancellationToken cancellationToken);
}
