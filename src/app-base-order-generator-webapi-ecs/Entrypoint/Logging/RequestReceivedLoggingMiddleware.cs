using Base.OrderGenerator.Commons.Logging;

namespace Base.OrderGenerator.Entrypoint.Logging;

public sealed class RequestReceivedLoggingMiddleware
{
    public const string ApiRoutesPrefix = "/api";

    private readonly RequestDelegate _nextMiddleware;
    private readonly IApplicationLogger<RequestReceivedLoggingMiddleware> _logger;

    public RequestReceivedLoggingMiddleware(RequestDelegate nextMiddleware, IApplicationLogger<RequestReceivedLoggingMiddleware> logger)
    {
        _nextMiddleware = nextMiddleware ?? throw new ArgumentNullException(nameof(nextMiddleware));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task InvokeAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (httpContext.Request.Path.StartsWithSegments(ApiRoutesPrefix, StringComparison.OrdinalIgnoreCase))
            _logger.LogInformation("Request received.", new { Method = httpContext.Request.Method, Path = httpContext.Request.Path.Value });

        return _nextMiddleware(httpContext);
    }
}
