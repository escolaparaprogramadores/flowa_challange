using System.Net;
using System.Text.Json;
using Flowa.OrderGenerator.Application.Orders.Interfaces;
using Flowa.Commons.Http;
using Flowa.Commons.Responses;

namespace Flowa.OrderGenerator.Infrastructure.Orders.Adapters;

internal sealed class HttpStoredOrdersAdapter : IStoredOrdersPort
{
    public const string OrderAccumulatorApiName = "OrderAccumulator";

    private readonly IHttpRequestClient _httpRequestClient;

    public HttpStoredOrdersAdapter(IHttpRequestClient httpRequestClient)
    {
        _httpRequestClient = httpRequestClient ?? throw new ArgumentNullException(nameof(httpRequestClient));
    }

    public async Task<DataMessage<JsonElement>> GetStoredOrdersPageAsync(string? requestedPageNumber, CancellationToken cancellationToken)
    {
        var ordersPagePath = requestedPageNumber is null ? "/api/orders" : $"/api/orders?page={Uri.EscapeDataString(requestedPageNumber)}";
        var ordersPageResponse = await _httpRequestClient.SendGetRequestAsync(OrderAccumulatorApiName, ordersPagePath, cancellationToken);
        if (ordersPageResponse.StatusCode == HttpStatusCode.BadRequest)
            return ordersPageResponse.ReadInvalidInputDataMessage();

        ordersPageResponse.EnsureHttpStatusCode(HttpStatusCode.OK);
        return ordersPageResponse.ReadSuccessDataMessage();
    }

    public async Task DeleteAllStoredOrdersAsync(CancellationToken cancellationToken)
    {
        var ordersDeletionResponse = await _httpRequestClient.SendDeleteRequestAsync(OrderAccumulatorApiName, "/api/orders", cancellationToken);
        ordersDeletionResponse.EnsureHttpStatusCode(HttpStatusCode.NoContent);
    }
}
