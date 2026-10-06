using System.Net;
using System.Text.Json;
using Flowa.OrderGenerator.Application.Exposures.Interfaces;
using Flowa.OrderGenerator.Commons.Http;
using Flowa.OrderGenerator.Commons.Responses;
using Flowa.OrderGenerator.Infrastructure.Orders.Adapters;

namespace Flowa.OrderGenerator.Infrastructure.Exposures.Adapters;

internal sealed class HttpSymbolExposuresAdapter : ISymbolExposuresPort
{
    private readonly IHttpRequestClient _httpRequestClient;

    public HttpSymbolExposuresAdapter(IHttpRequestClient httpRequestClient)
    {
        _httpRequestClient = httpRequestClient ?? throw new ArgumentNullException(nameof(httpRequestClient));
    }

    public async Task<DataMessage<JsonElement>> GetSymbolExposuresAsync(CancellationToken cancellationToken)
    {
        var exposuresResponse = await _httpRequestClient.SendGetRequestAsync(HttpStoredOrdersAdapter.OrderAccumulatorApiName, "/api/exposures", cancellationToken);
        exposuresResponse.EnsureHttpStatusCode(HttpStatusCode.OK);
        return exposuresResponse.ReadSuccessDataMessage();
    }
}
