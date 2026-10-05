using Base.OrderAccumulator.Commons;
using StatsdClient;

namespace Base.OrderAccumulator.Infrastructure.Metrics;

// Counts the order after the database decided, with the names and tags agreed with the dashboard.
public sealed class DatadogOrderMetricsAdapter(IDogStatsd orderMetricsClient) : IOrderMetrics
{
    public void CountAnsweredOrder(string? orderSymbol, char orderSide, bool orderAccepted)
    {
        var answeredOrderTags = OrderMetricTagMapper.BuildOrderDecisionTags(orderSymbol, orderSide);
        if (orderAccepted)
        {
            orderMetricsClient.Increment(OrderMetricNames.AcceptedOrders, tags: answeredOrderTags);
        }
        else
        {
            orderMetricsClient.Increment(OrderMetricNames.RejectedOrders, tags: answeredOrderTags);
        }
    }

    public void SendSymbolExposureGauge(string orderSymbol, decimal symbolExposure) =>
        orderMetricsClient.Gauge(
            OrderMetricNames.SymbolExposure, (double)symbolExposure, tags: OrderMetricTagMapper.BuildSymbolExposureTags(orderSymbol));
}
