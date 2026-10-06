using Base.OrderGenerator.Commons.Logging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Base.OrderGenerator.Entrypoint.ErrorHandling;

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

        if (problemDetailsContext.Exception is null)
            responseProblemDetails.Extensions["traceId"] = HttpErrorTraceScope.WriteUnderHttpErrorTrace(httpContext, null, () =>
                httpErrorLogger.LogWarning("Expected error in request.", new
                {
                    ErrorCode = responseProblemDetails.Type,
                    Method = httpContext.Request.Method,
                    Route = ApiProblemDetailsExtensions.ReadRouteTemplate(httpContext)
                }));

        return new ValueTask(httpContext.Response.WriteAsJsonAsync(
            responseProblemDetails, responseProblemDetails.GetType(), jsonOptions.Value.SerializerOptions, "application/problem+json"));
    }
}
