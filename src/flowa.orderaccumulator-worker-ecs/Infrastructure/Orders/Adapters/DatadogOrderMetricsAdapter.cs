using Flowa.OrderAccumulator.Application.Orders.Interfaces;
using Flowa.Commons.Observability;

namespace Flowa.OrderAccumulator.Infrastructure.Orders.Adapters;

public sealed class DatadogOrderMetricsAdapter : IOrderMetricsPort
{
    private readonly IMetricsClient orderMetricsClient;

    public DatadogOrderMetricsAdapter(IMetricsClient orderMetricsClient)
    {
        this.orderMetricsClient = orderMetricsClient ?? throw new ArgumentNullException(nameof(orderMetricsClient));
    }

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
