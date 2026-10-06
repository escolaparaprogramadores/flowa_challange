using Flowa.OrderGenerator.Commons.Logging;
using Flowa.OrderGenerator.Entrypoint.ErrorHandling;

namespace Flowa.OrderGenerator.Entrypoint.Logging;

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

        if (httpContext.Request.Path.StartsWithSegments(ApiRoutesPrefix, StringComparison.OrdinalIgnoreCase) && MatchedAKnownApiRoute(httpContext))
            _logger.LogInformation("Request received.", new { Method = httpContext.Request.Method, Route = ApiProblemDetailsExtensions.ReadRouteTemplate(httpContext) });

        return _nextMiddleware(httpContext);
    }

    private static bool MatchedAKnownApiRoute(HttpContext httpContext) =>
        httpContext.GetEndpoint() is RouteEndpoint matchedRouteEndpoint
        && !matchedRouteEndpoint.RoutePattern.Parameters.Any(routeParameter => routeParameter.IsCatchAll);
}
