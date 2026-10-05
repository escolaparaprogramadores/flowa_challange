using System.Text.Json;
using Base.OrderGenerator.Commons;

namespace Base.OrderGenerator.Application.Orders.ListOrders;

// One page of the orders the OrderAccumulator stored; the OrderAccumulator checks the page number.
public sealed class ListOrdersUseCase(IOrderAccumulatorHttpClient orderAccumulatorHttpClient)
{
    public Task<DataMessage<JsonElement>> ListOrdersAsync(string? requestedPageNumber, CancellationToken cancellationToken) =>
        orderAccumulatorHttpClient.GetStoredOrdersPageAsync(requestedPageNumber, cancellationToken);
}
