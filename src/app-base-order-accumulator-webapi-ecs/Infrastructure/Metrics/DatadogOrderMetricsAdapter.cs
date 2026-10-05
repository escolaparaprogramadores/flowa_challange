using OrderAccumulator.Exposure;
using StatsdClient;

namespace OrderAccumulator.Observabilidade;

// Conta a ordem depois que o banco decidiu. Repetição devolve a resposta antiga e não conta de novo;
// exceção passa direto, sem contar.
public sealed class OrderProcessorWithMetrics(
    IOrderProcessor storedOrderProcessor, IDogStatsd orderMetricsClient, SymbolExposureMemory symbolExposureMemory) : IOrderProcessor
{
    public async Task<OrderOutcome> ProcessIncomingOrderAsync(IncomingOrder incomingOrder, CancellationToken cancellationToken = default)
    {
        // Gravar no banco e somar na memória acontecem juntos, sem um "Deletar tudo" no meio.
        var orderOutcome = await symbolExposureMemory.ProcessOrderOutsideDeleteAllAsync(async () =>
        {
            var storedOrderOutcome = await storedOrderProcessor.ProcessIncomingOrderAsync(incomingOrder, cancellationToken);
            if (storedOrderOutcome is { IsRepeat: false, Accepted: true })
                symbolExposureMemory.ApplyAcceptedOrder(storedOrderOutcome);
            return storedOrderOutcome;
        }, cancellationToken);
        if (orderOutcome.IsRepeat)
            return orderOutcome;

        var orderOutcomeTags = OrderMetricTags.OrderOutcomeTags(orderOutcome.Symbol, orderOutcome.Side);
        if (orderOutcome.Accepted)
        {
            orderMetricsClient.Increment(OrderMetricNames.AcceptedOrders, tags: orderOutcomeTags);
        }
        else
        {
            orderMetricsClient.Increment(OrderMetricNames.RejectedOrders, tags: orderOutcomeTags);
        }

        return orderOutcome;
    }
}
