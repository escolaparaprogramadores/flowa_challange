using Base.OrderGenerator.Commons;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Base.OrderGenerator.Entrypoint.Errors;

// The default ASP.NET writer only writes for a caller that accepts JSON: with "Accept: text/html" the error left
// without traceId and without log. An API answers in its contract to any caller.
public sealed class ProblemDetailsForAnyClientWriter(
    IOptions<ProblemDetailsOptions> problemDetailsOptions,
    IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions,
    IApplicationLogger<GlobalErrorHandler> httpErrorLogger) : IProblemDetailsWriter
{
    public bool CanWrite(ProblemDetailsContext problemDetailsContext) => true;

    public ValueTask WriteAsync(ProblemDetailsContext problemDetailsContext)
    {
        problemDetailsOptions.Value.CustomizeProblemDetails?.Invoke(problemDetailsContext);
        var responseProblemDetails = problemDetailsContext.ProblemDetails;
        var httpContext = problemDetailsContext.HttpContext;

        // An error that came from an exception was already logged by the GlobalErrorHandler; here comes the rest (an
        // error DataMessage, the 404 of ASP.NET), so every HTTP error has exactly one log. A technical failure always
        // arrives as an exception, so what arrives here without one is expected: Warning.
        if (problemDetailsContext.Exception is null)
            responseProblemDetails.Extensions["traceId"] = HttpErrorTraceScope.WriteUnderHttpErrorTrace(httpContext, null, () =>
                httpErrorLogger.LogWarning("Expected error in request.", new
                {
                    ErrorCode = responseProblemDetails.Type,
                    Method = httpContext.Request.Method,
                    Route = ApiProblemDetails.ReadRouteTemplate(httpContext)
                }));

        return new ValueTask(httpContext.Response.WriteAsJsonAsync(
            responseProblemDetails, responseProblemDetails.GetType(), jsonOptions.Value.SerializerOptions, "application/problem+json"));
    }
}
