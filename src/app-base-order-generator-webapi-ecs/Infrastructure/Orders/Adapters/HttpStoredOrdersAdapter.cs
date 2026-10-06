using System.Net;
using System.Text.Json;
using Base.OrderGenerator.Application.Orders.Interfaces;
using Base.OrderGenerator.Commons.Http;
using Base.OrderGenerator.Commons.Responses;

namespace Base.OrderGenerator.Infrastructure.Orders.Adapters;

public sealed class HttpStoredOrdersAdapter(IHttpRequestClient httpRequestClient) : IStoredOrdersPort
{
    public const string OrderAccumulatorApiName = "OrderAccumulator";

    public async Task<DataMessage<JsonElement>> GetStoredOrdersPageAsync(string? requestedPageNumber, CancellationToken cancellationToken)
    {
        var ordersPagePath = requestedPageNumber is null ? "/api/orders" : $"/api/orders?page={Uri.EscapeDataString(requestedPageNumber)}";
        var ordersPageResponse = await httpRequestClient.SendGetRequestAsync(OrderAccumulatorApiName, ordersPagePath, cancellationToken);
        if (ordersPageResponse.StatusCode == HttpStatusCode.BadRequest)
            return ordersPageResponse.ReadInvalidInputDataMessage();

        ordersPageResponse.EnsureHttpStatusCode(HttpStatusCode.OK);
        return ordersPageResponse.ReadSuccessDataMessage();
    }

    public async Task DeleteAllStoredOrdersAsync(CancellationToken cancellationToken)
    {
        var ordersDeletionResponse = await httpRequestClient.SendDeleteRequestAsync(OrderAccumulatorApiName, "/api/orders", cancellationToken);
        ordersDeletionResponse.EnsureHttpStatusCode(HttpStatusCode.NoContent);
    }
}
