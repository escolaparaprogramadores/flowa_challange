using Base.OrderAccumulator.Application.Orders.Responses;

namespace Base.OrderAccumulator.Application.Orders.Interfaces;

public interface IOrderListReadRepository
{
    Task<StoredOrderPageResponse> ReadStoredOrderPageAsync(int pageNumber, CancellationToken cancellationToken = default);
}
