using Base.OrderAccumulator.Application.Orders.Interfaces;
using Base.OrderAccumulator.Commons.Observability;

namespace Base.OrderAccumulator.Infrastructure.Orders.Adapters;

public sealed class DatadogOrderMetricsAdapter(IMetricsClient orderMetricsClient) : IOrderMetricsPort
{
    public void CountAnsweredOrder(string? orderSymbol, char orderSide, bool orderAccepted)
    {
        var answeredOrderTags = OrderMetricTagMapper.BuildOrderDecisionTags(orderSymbol, orderSide);
        if (orderAccepted)
        {
            orderMetricsClient.IncrementCounter(OrderMetricNames.AcceptedOrders, answeredOrderTags);
        }
        else
        {
            orderMetricsClient.IncrementCounter(OrderMetricNames.RejectedOrders, answeredOrderTags);
        }
    }

    public void SendSymbolExposureGauge(string orderSymbol, decimal symbolExposure) =>
        orderMetricsClient.RecordGauge(
            OrderMetricNames.SymbolExposure, (double)symbolExposure, OrderMetricTagMapper.BuildSymbolExposureTags(orderSymbol));
}
