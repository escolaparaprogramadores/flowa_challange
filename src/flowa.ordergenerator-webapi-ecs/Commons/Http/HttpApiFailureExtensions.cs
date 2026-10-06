namespace Flowa.OrderGenerator.Commons.Http;

public static class HttpApiFailureExtensions
{
    extension(Exception failure)
    {
        public bool IndicatesUnavailableHttpApi() =>
            failure is HttpRequestException or TaskCanceledException { InnerException: TimeoutException };
    }
}
