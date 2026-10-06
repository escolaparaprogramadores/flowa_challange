using Base.OrderAccumulator.Commons.Logging;
using Base.OrderAccumulator.Commons.Responses;
using Microsoft.AspNetCore.Diagnostics;

namespace Base.OrderAccumulator.Entrypoint.ErrorHandling;

public sealed class GlobalErrorHandler(IProblemDetailsService problemDetailsService, IApplicationLogger<GlobalErrorHandler> httpErrorLogger) : IExceptionHandler
{
    public const string InvalidRequestMessage = "Dados inválidos";
    public const string UnexpectedErrorMessage = "Aconteceu um erro inesperado. Informe o traceId ao suporte.";

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var errorProblemDetails = exception is BadHttpRequestException
            ? ApiProblemDetailsExtensions.BuildErrorProblemDetails(ResultStatus.InvalidInput, null, InvalidRequestMessage, [])
            : ApiProblemDetailsExtensions.BuildErrorProblemDetails(ResultStatus.InternalError, null, UnexpectedErrorMessage, []);

        var httpErrorLogContext = new { ErrorCode = errorProblemDetails.Type, Method = httpContext.Request.Method, Route = ApiProblemDetailsExtensions.ReadRouteTemplate(httpContext) };
        errorProblemDetails.Extensions["traceId"] = HttpErrorTraceScope.WriteUnderHttpErrorTrace(httpContext, () =>
        {
            if (errorProblemDetails.Status == StatusCodes.Status500InternalServerError)
                httpErrorLogger.LogError(exception, "Unexpected application error.", httpErrorLogContext);
            else
                httpErrorLogger.LogWarning("Expected error in request.", httpErrorLogContext);
        });

        httpContext.Response.StatusCode = errorProblemDetails.Status!.Value;
        await problemDetailsService.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = errorProblemDetails
        });
        return true;
    }
}
