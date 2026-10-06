using Base.OrderGenerator.Commons.Logging;
using Base.OrderGenerator.Commons.Responses;
using Microsoft.AspNetCore.Mvc;

namespace Base.OrderGenerator.Entrypoint.ErrorHandling;

public sealed class DataMessageHttpResponseConverter
{
    public const string TraceIdExtensionName = "traceId";

    private readonly IApplicationLogger<OrderGeneratorExceptionHandler> _httpErrorLogger;

    public DataMessageHttpResponseConverter(IApplicationLogger<OrderGeneratorExceptionHandler> httpErrorLogger)
    {
        _httpErrorLogger = httpErrorLogger ?? throw new ArgumentNullException(nameof(httpErrorLogger));
    }

    public IResult ConvertToHttpResponse<T>(DataMessage<T> useCaseMessage, HttpContext httpContext) =>
        ConvertToHttpResponse(useCaseMessage, httpContext, useCaseData => useCaseData);

    public IResult ConvertToHttpResponse<T, TResponseData>(DataMessage<T> useCaseMessage, HttpContext httpContext, Func<T, TResponseData> convertToResponseData)
    {
        ArgumentNullException.ThrowIfNull(useCaseMessage);
        ArgumentNullException.ThrowIfNull(httpContext);

        if (useCaseMessage.Success)
            return Results.Json(
                DataMessage<TResponseData>.CreateSuccessMessage(convertToResponseData(useCaseMessage.Data!), useCaseMessage.Message, useCaseMessage.Status),
                statusCode: ResultStatusHttpMapper.ConvertToHttpStatusCode(useCaseMessage.Status));

        var errorProblemDetails = ApiProblemDetailsExtensions.BuildErrorProblemDetails(
            useCaseMessage.Status, useCaseMessage.ErrorCode, useCaseMessage.Message, useCaseMessage.Errors);
        WriteHttpErrorLogUnderItsTrace(httpContext, errorProblemDetails, useCaseMessage.Failure, useCaseMessage.FailureTraceId);
        return Results.Problem(errorProblemDetails);
    }

    public void WriteHttpErrorLogUnderItsTrace(HttpContext httpContext, ProblemDetails errorProblemDetails, Exception? httpFailure, string? failureTraceId)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(errorProblemDetails);

        var httpErrorLogContext = new
        {
            ErrorCode = errorProblemDetails.Type,
            Method = httpContext.Request.Method,
            Route = ApiProblemDetailsExtensions.ReadRouteTemplate(httpContext)
        };
        errorProblemDetails.Extensions[TraceIdExtensionName] = HttpErrorTraceScope.WriteUnderHttpErrorTrace(httpContext, failureTraceId, () =>
        {
            if (errorProblemDetails.Status == StatusCodes.Status500InternalServerError && httpFailure is not null)
                _httpErrorLogger.LogError(httpFailure, "Unexpected application error.", httpErrorLogContext);
            else
                _httpErrorLogger.LogWarning("Expected error in request.", httpErrorLogContext);
        });
    }
}
