using Base.OrderGenerator.Commons.Http;
using Base.OrderGenerator.Commons.Logging;
using Base.OrderGenerator.Commons.Responses;
using Base.OrderGenerator.Domain.Orders.Exceptions;
using Microsoft.AspNetCore.Diagnostics;

namespace Base.OrderGenerator.Entrypoint.ErrorHandling;

public sealed class GlobalErrorHandler(IProblemDetailsService problemDetailsService, IApplicationLogger<GlobalErrorHandler> httpErrorLogger) : IExceptionHandler
{
    public const string InvalidRequestMessage = "Dados inválidos";
    public const string UnexpectedErrorMessage = "Aconteceu um erro inesperado. Informe o traceId ao suporte.";
    public const string OrderAccumulatorUnavailableMessage = "Não foi possível falar com o OrderAccumulator. Tente de novo em instantes.";
    public const string OrderAccumulatorUnavailableErrorCode = "order-accumulator-unavailable";

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var errorProblemDetails = exception switch
        {
            BadHttpRequestException => ApiProblemDetailsExtensions.BuildErrorProblemDetails(ResultStatus.InvalidInput, null, InvalidRequestMessage, []),
            OrderNotAnsweredException orderNotAnswered => ApiProblemDetailsExtensions.BuildErrorProblemDetails(
                ResultStatus.ServiceUnavailable, orderNotAnswered.ErrorCode, OrderAccumulatorUnavailableMessage, []),
            _ when exception.IndicatesUnavailableHttpApi() => ApiProblemDetailsExtensions.BuildErrorProblemDetails(
                ResultStatus.ServiceUnavailable, OrderAccumulatorUnavailableErrorCode, OrderAccumulatorUnavailableMessage, []),
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
        return true;
    }
}
