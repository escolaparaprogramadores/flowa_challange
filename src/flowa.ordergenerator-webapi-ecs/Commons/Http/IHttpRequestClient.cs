namespace Flowa.OrderGenerator.Commons.Http;

public interface IHttpRequestClient
{
    Task<HttpApiResponse> SendGetRequestAsync(string apiName, string resourcePath, CancellationToken cancellationToken);

    Task<HttpApiResponse> SendDeleteRequestAsync(string apiName, string resourcePath, CancellationToken cancellationToken);
}
