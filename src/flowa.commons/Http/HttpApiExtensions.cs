using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Flowa.Commons.Http;

public static class HttpApiExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddHttpApi(string apiName, Func<IServiceProvider, string> readApiBaseUrl, TimeSpan requestTimeout)
        {
            services.AddHttpClient(apiName, (serviceProvider, apiHttpClient) =>
            {
                apiHttpClient.BaseAddress = new Uri(readApiBaseUrl(serviceProvider));
                apiHttpClient.Timeout = requestTimeout;
            });
            services.TryAddSingleton<IHttpRequestClient, HttpRequestClient>();
            return services;
        }
    }
}
