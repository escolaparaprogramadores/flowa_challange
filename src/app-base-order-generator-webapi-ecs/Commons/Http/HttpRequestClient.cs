namespace Base.OrderGenerator.Commons.Http;

public sealed class HttpRequestClient(IHttpClientFactory httpClientFactory) : IHttpRequestClient
{
    public async Task<HttpApiResponse> SendGetRequestAsync(string apiName, string resourcePath, CancellationToken cancellationToken)
    {
        using var httpResponse = await httpClientFactory.CreateClient(apiName).GetAsync(resourcePath, cancellationToken);
        return await ReadHttpApiResponseAsync(apiName, httpResponse, cancellationToken);
    }

    public async Task<HttpApiResponse> SendDeleteRequestAsync(string apiName, string resourcePath, CancellationToken cancellationToken)
    {
        using var httpResponse = await httpClientFactory.CreateClient(apiName).DeleteAsync(resourcePath, cancellationToken);
        return await ReadHttpApiResponseAsync(apiName, httpResponse, cancellationToken);
    }

    private static async Task<HttpApiResponse> ReadHttpApiResponseAsync(string apiName, HttpResponseMessage httpResponse, CancellationToken cancellationToken) =>
        new(apiName, httpResponse.StatusCode, httpResponse.Content.Headers.ContentType?.MediaType,
            await httpResponse.Content.ReadAsByteArrayAsync(cancellationToken));
}
