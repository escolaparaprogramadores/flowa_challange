using Flowa.Commons.Logging;
using Microsoft.Extensions.Options;

namespace Flowa.OrderAccumulator.Entrypoint.ErrorHandling;

public sealed class ProblemDetailsForAnyClientWriter : IProblemDetailsWriter
{
    private readonly IOptions<ProblemDetailsOptions> problemDetailsOptions;
    private readonly IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions;
    private readonly IApplicationLogger<GlobalErrorHandler> httpErrorLogger;

    public ProblemDetailsForAnyClientWriter(
        IOptions<ProblemDetailsOptions> problemDetailsOptions,
        IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions,
        IApplicationLogger<GlobalErrorHandler> httpErrorLogger)
    {
        this.problemDetailsOptions = problemDetailsOptions ?? throw new ArgumentNullException(nameof(problemDetailsOptions));
        this.jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
        this.httpErrorLogger = httpErrorLogger ?? throw new ArgumentNullException(nameof(httpErrorLogger));
    }

    public bool CanWrite(ProblemDetailsContext problemDetailsContext) => true;

    public ValueTask WriteAsync(ProblemDetailsContext problemDetailsContext)
    {
        problemDetailsOptions.Value.CustomizeProblemDetails?.Invoke(problemDetailsContext);
        var responseProblemDetails = problemDetailsContext.ProblemDetails;
        var httpContext = problemDetailsContext.HttpContext;

        if (problemDetailsContext.Exception is null)
            responseProblemDetails.Extensions["traceId"] = HttpErrorTraceScope.WriteUnderHttpErrorTrace(httpContext, () =>
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
