using Base.OrderGenerator.Commons;
using Microsoft.AspNetCore.Diagnostics;

namespace Base.OrderGenerator.Entrypoint.Errors;

// The one place where an exception becomes an HTTP answer and its single log line (backend-tratamento-erro.md).
public sealed class GlobalErrorHandler(IProblemDetailsService problemDetailsService, IApplicationLogger<GlobalErrorHandler> httpErrorLogger) : IExceptionHandler
{
    public const string InvalidRequestMessage = "Dados inválidos";
    public const string UnexpectedErrorMessage = "Aconteceu um erro inesperado. Informe o traceId ao suporte.";
    public const string OrderAccumulatorUnavailableErrorCode = "order-accumulator-unavailable";

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var errorProblemDetails = exception switch
        {
            BadHttpRequestException => ApiProblemDetailsExtensions.BuildErrorProblemDetails(ResultStatus.InvalidInput, null, InvalidRequestMessage, []),
            OrderNotAnsweredException orderNotAnswered => ApiProblemDetailsExtensions.BuildErrorProblemDetails(
                ResultStatus.ServiceUnavailable, orderNotAnswered.ErrorCode, OrderAccumulatorMessages.OrderAccumulatorUnavailableMessage, []),
            _ when IsOrderAccumulatorUnavailable(exception) => ApiProblemDetailsExtensions.BuildErrorProblemDetails(
                ResultStatus.ServiceUnavailable, OrderAccumulatorUnavailableErrorCode, OrderAccumulatorMessages.OrderAccumulatorUnavailableMessage, []),
            _ => ApiProblemDetailsExtensions.BuildErrorProblemDetails(ResultStatus.InternalError, null, UnexpectedErrorMessage, [])
        };

        var httpErrorLogContext = new { ErrorCode = errorProblemDetails.Type, Method = httpContext.Request.Method, Route = ApiProblemDetailsExtensions.ReadRouteTemplate(httpContext) };
        void WriteHttpErrorLog()
        {
            if (errorProblemDetails.Status == StatusCodes.Status500InternalServerError)
                httpErrorLogger.LogError(exception, "Unexpected application error.", httpErrorLogContext);
            else
                httpErrorLogger.LogWarning("Expected error in request.", httpErrorLogContext);
        }

        errorProblemDetails.Extensions["traceId"] = HttpErrorTraceScope.WriteUnderHttpErrorTrace(
            httpContext, (exception as OrderFailureException)?.ClOrdId, WriteHttpErrorLog);

        httpContext.Response.StatusCode = errorProblemDetails.Status!.Value;
        await problemDetailsService.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = errorProblemDetails
        });
        // Returning false would make the ExceptionHandlerMiddleware log the same exception again.
        return true;
    }

    // The OrderAccumulator refused the connection, answered outside the contract, or the 5 s of the HttpClient ran out.
    private static bool IsOrderAccumulatorUnavailable(Exception exception) =>
        exception is HttpRequestException or TaskCanceledException { InnerException: TimeoutException };
}
