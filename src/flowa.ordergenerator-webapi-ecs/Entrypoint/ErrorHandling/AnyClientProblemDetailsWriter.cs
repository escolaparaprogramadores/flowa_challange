using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Flowa.OrderGenerator.Entrypoint.ErrorHandling;

public sealed class AnyClientProblemDetailsWriter : IProblemDetailsWriter
{
    private readonly IOptions<ProblemDetailsOptions> _problemDetailsOptions;
    private readonly IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> _jsonOptions;
    private readonly DataMessageHttpResponseConverter _dataMessageHttpResponseConverter;

    public AnyClientProblemDetailsWriter(
        IOptions<ProblemDetailsOptions> problemDetailsOptions,
        IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions,
        DataMessageHttpResponseConverter dataMessageHttpResponseConverter)
    {
        _problemDetailsOptions = problemDetailsOptions ?? throw new ArgumentNullException(nameof(problemDetailsOptions));
        _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
        _dataMessageHttpResponseConverter = dataMessageHttpResponseConverter ?? throw new ArgumentNullException(nameof(dataMessageHttpResponseConverter));
    }

    public bool CanWrite(ProblemDetailsContext problemDetailsContext) => true;

    public ValueTask WriteAsync(ProblemDetailsContext problemDetailsContext)
    {
        _problemDetailsOptions.Value.CustomizeProblemDetails?.Invoke(problemDetailsContext);
        var responseProblemDetails = problemDetailsContext.ProblemDetails;
        var httpContext = problemDetailsContext.HttpContext;

        if (!responseProblemDetails.Extensions.ContainsKey(DataMessageHttpResponseConverter.TraceIdExtensionName))
            _dataMessageHttpResponseConverter.WriteHttpErrorLogUnderItsTrace(httpContext, responseProblemDetails, null, null);

        return new ValueTask(httpContext.Response.WriteAsJsonAsync(
            responseProblemDetails, responseProblemDetails.GetType(), _jsonOptions.Value.SerializerOptions, "application/problem+json"));
    }
}
