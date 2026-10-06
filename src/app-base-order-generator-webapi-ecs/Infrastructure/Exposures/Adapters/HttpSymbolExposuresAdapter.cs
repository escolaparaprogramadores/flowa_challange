using System.Net;
using System.Text.Json;
using Base.OrderGenerator.Application.Exposures.Interfaces;
using Base.OrderGenerator.Commons.Http;
using Base.OrderGenerator.Commons.Responses;
using Base.OrderGenerator.Infrastructure.Orders.Adapters;

namespace Base.OrderGenerator.Infrastructure.Exposures.Adapters;

public sealed class HttpSymbolExposuresAdapter(IHttpRequestClient httpRequestClient) : ISymbolExposuresPort
{
    public async Task<DataMessage<JsonElement>> GetSymbolExposuresAsync(CancellationToken cancellationToken)
    {
        var exposuresResponse = await httpRequestClient.SendGetRequestAsync(HttpStoredOrdersAdapter.OrderAccumulatorApiName, "/api/exposures", cancellationToken);
        exposuresResponse.EnsureHttpStatusCode(HttpStatusCode.OK);
        return exposuresResponse.ReadSuccessDataMessage();
    }
}
