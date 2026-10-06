using Flowa.Commons.Observability;
using Flowa.DatadogMetrics.Application.Orders.Interfaces;
using Flowa.DatadogMetrics.Domain.Orders.ValueObjects;

namespace Flowa.DatadogMetrics.Infrastructure.Orders.Adapters;

internal sealed class DatadogOrderMetricsAdapter : IOrderMetricsPort
{
    private readonly IMetricsClient datadogMetricsClient;

    public DatadogOrderMetricsAdapter(IMetricsClient datadogMetricsClient)
    {
        this.datadogMetricsClient = datadogMetricsClient ?? throw new ArgumentNullException(nameof(datadogMetricsClient));
    }

    public void SendAnsweredOrderCounts(IReadOnlyList<AnsweredOrderCount> answeredOrderCounts)
    {
        var orderCountsByMetricAndTags = answeredOrderCounts
            .GroupBy(answeredOrderCount => (
                MetricName: answeredOrderCount.Accepted ? OrderMetricNames.AcceptedOrders : OrderMetricNames.RejectedOrders,
                TagKey: OrderMetricTagMapper.BuildOrderDecisionTagKey(answeredOrderCount.Symbol, answeredOrderCount.Side)));

        foreach (var orderCountOfMetricAndTags in orderCountsByMetricAndTags)
        {
            var orderDecisionTags = orderCountOfMetricAndTags.Key.TagKey.Split(',');
            var answeredOrdersOfMetricAndTags = orderCountOfMetricAndTags.Sum(answeredOrderCount => answeredOrderCount.OrderCount);
            for (var countedOrder = 0L; countedOrder < answeredOrdersOfMetricAndTags; countedOrder++)
                datadogMetricsClient.IncrementCounter(orderCountOfMetricAndTags.Key.MetricName, orderDecisionTags);
        }
    }
}
