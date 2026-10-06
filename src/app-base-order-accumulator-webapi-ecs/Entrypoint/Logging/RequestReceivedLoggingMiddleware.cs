using Base.OrderAccumulator.Commons.Logging;

namespace Base.OrderAccumulator.Entrypoint.Logging;

public sealed class RequestReceivedLoggingMiddleware
{
    public const string ApiRoutesPrefix = "/api";

    private readonly RequestDelegate nextMiddleware;
    private readonly IApplicationLogger<RequestReceivedLoggingMiddleware> requestLogger;

    public RequestReceivedLoggingMiddleware(RequestDelegate nextMiddleware, IApplicationLogger<RequestReceivedLoggingMiddleware> requestLogger)
    {
        this.nextMiddleware = nextMiddleware ?? throw new ArgumentNullException(nameof(nextMiddleware));
        this.requestLogger = requestLogger ?? throw new ArgumentNullException(nameof(requestLogger));
    }

    public Task InvokeAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (httpContext.Request.Path.StartsWithSegments(ApiRoutesPrefix, StringComparison.OrdinalIgnoreCase))
            requestLogger.LogInformation("Request received.", new { Method = httpContext.Request.Method, Path = httpContext.Request.Path.Value });

        return nextMiddleware(httpContext);
    }
}
