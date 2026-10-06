using Flowa.OrderAccumulator.Application.Orders.Responses;

namespace Flowa.OrderAccumulator.Application.Orders.Interfaces;

public interface IOrderListReadRepository
{
    Task<StoredOrderPageResponse> ReadStoredOrderPageAsync(int pageNumber, CancellationToken cancellationToken = default);
}
