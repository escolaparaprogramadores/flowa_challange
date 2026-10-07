using Flowa.DatadogMetrics.Domain.Orders.ValueObjects;

namespace Flowa.DatadogMetrics.Application.Orders.Interfaces;

public interface IOrderMetricsPort
{
    void SendAnsweredOrderCounts(IReadOnlyList<AnsweredOrderCount> answeredOrderCounts);
}
