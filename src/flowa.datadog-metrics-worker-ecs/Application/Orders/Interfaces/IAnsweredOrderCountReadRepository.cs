using Flowa.DatadogMetrics.Domain.Orders.ValueObjects;

namespace Flowa.DatadogMetrics.Application.Orders.Interfaces;

public interface IAnsweredOrderCountReadRepository
{
    Task<long> GetLastStoredOrderIdAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AnsweredOrderCount>> GetAnsweredOrderCountsAfterAsync(long lastCountedOrderId, CancellationToken cancellationToken = default);
}
