using System.Diagnostics;
using Base.OrderGenerator.Commons.Logging;

namespace Base.OrderGenerator.Commons.Http;

public sealed class HttpRequestClient : IHttpRequestClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IApplicationLogger<HttpRequestClient> _logger;

    public HttpRequestClient(IHttpClientFactory httpClientFactory, IApplicationLogger<HttpRequestClient> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<HttpApiResponse> SendGetRequestAsync(string apiName, string resourcePath, CancellationToken cancellationToken)
    {
        var callStart = Stopwatch.GetTimestamp();
        using var httpResponse = await _httpClientFactory.CreateClient(apiName).GetAsync(resourcePath, cancellationToken);
        return await ReadHttpApiResponseAsync(apiName, HttpMethod.Get, resourcePath, httpResponse, callStart, cancellationToken);
    }

    public async Task<HttpApiResponse> SendDeleteRequestAsync(string apiName, string resourcePath, CancellationToken cancellationToken)
    {
        var callStart = Stopwatch.GetTimestamp();
        using var httpResponse = await _httpClientFactory.CreateClient(apiName).DeleteAsync(resourcePath, cancellationToken);
        return await ReadHttpApiResponseAsync(apiName, HttpMethod.Delete, resourcePath, httpResponse, callStart, cancellationToken);
    }

    private async Task<HttpApiResponse> ReadHttpApiResponseAsync(string apiName, HttpMethod httpMethod, string resourcePath,
        HttpResponseMessage httpResponse, long callStart, CancellationToken cancellationToken)
    {
        var httpApiResponse = new HttpApiResponse(apiName, httpResponse.StatusCode, httpResponse.Content.Headers.ContentType?.MediaType,
            await httpResponse.Content.ReadAsByteArrayAsync(cancellationToken));
        _logger.LogInformation("HTTP call completed.", new
        {
            ApiName = apiName,
            HttpMethod = httpMethod.Method,
            ResourcePath = resourcePath.Split('?')[0],
            StatusCode = (int)httpResponse.StatusCode,
            DurationMs = Stopwatch.GetElapsedTime(callStart).TotalMilliseconds
        });
        return httpApiResponse;
    }
}
