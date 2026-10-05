using Base.OrderAccumulator.Commons;
using Base.OrderAccumulator.Entrypoint.Http;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Base.OrderAccumulator.Entrypoint.Errors;

// HTTP contract of the /api routes (backend-problem-details.md): success as DataMessage, error as problem+json.
public static class ApiProblemDetailsExtensions
{
    public const string ProblemTypePrefix = "urn:base-investimentos:problem:";

    public static IResult ConvertToHttpResponse<T>(this DataMessage<T> useCaseMessage) =>
        useCaseMessage.ConvertToHttpResponse(useCaseData => useCaseData);

    // The route promises its own body in "data"; status, message and errors stay the ones of the use case.
    public static IResult ConvertToHttpResponse<T, TResponseData>(this DataMessage<T> useCaseMessage, Func<T, TResponseData> convertToResponseData)
    {
        if (useCaseMessage.Success)
            return Results.Json(
                DataMessage<TResponseData>.CreateSuccessMessage(convertToResponseData(useCaseMessage.Data!), useCaseMessage.Message, useCaseMessage.Status),
                statusCode: ResultStatusHttpMapper.ConvertToHttpStatusCode(useCaseMessage.Status));

        // InternalError is a technical failure: it becomes an exception, so the GlobalErrorHandler logs Error with the
        // stack trace and answers the generic 500. The use case message never reaches the caller.
        if (useCaseMessage.Status == ResultStatus.InternalError)
            throw new InvalidOperationException(useCaseMessage.Message);

        return Results.Problem(BuildErrorProblemDetails(useCaseMessage.Status, useCaseMessage.ErrorCode, useCaseMessage.Message, useCaseMessage.Errors));
    }

    public static ProblemDetails BuildErrorProblemDetails(ResultStatus resultStatus, string? errorCode, string errorMessage, IReadOnlyCollection<string> errorMessages)
    {
        var errorProblemDetails = new ProblemDetails
        {
            Type = ProblemTypePrefix + (errorCode ?? DescribeDefaultErrorCode(resultStatus)),
            Title = DescribeDefaultTitle(resultStatus),
            Status = ResultStatusHttpMapper.ConvertToHttpStatusCode(resultStatus),
            Detail = errorMessage
        };
        errorProblemDetails.Extensions["statusResultado"] = resultStatus.ToString();
        errorProblemDetails.Extensions["errors"] = errorMessages;
        return errorProblemDetails;
    }

    // Runs on every problem+json, also the ones ASP.NET writes itself (404 of a route, 405, malformed JSON): that is
    // what gives all of them the URN type, the pt-BR text and the DataMessage compatibility fields.
    // The traceId comes from the place that writes the log line of the error (HttpErrorTraceScope).
    public static void CompleteProblemDetails(ProblemDetailsContext problemDetailsContext)
    {
        var responseProblemDetails = problemDetailsContext.ProblemDetails;
        var httpContext = problemDetailsContext.HttpContext;
        var httpStatusCode = responseProblemDetails.Status ?? httpContext.Response.StatusCode;
        var resultStatus = ResultStatusHttpMapper.ConvertToResultStatus(httpStatusCode);

        var problemCameFromTheFramework = responseProblemDetails.Type?.StartsWith(ProblemTypePrefix, StringComparison.Ordinal) != true;
        if (problemCameFromTheFramework)
        {
            var (frameworkErrorCode, frameworkTitle) = httpStatusCode == StatusCodes.Status405MethodNotAllowed
                ? ("method-not-allowed", "Método não permitido")
                : (DescribeDefaultErrorCode(resultStatus), DescribeDefaultTitle(resultStatus));
            responseProblemDetails.Type = ProblemTypePrefix + frameworkErrorCode;
            responseProblemDetails.Title = frameworkTitle;
            responseProblemDetails.Detail = frameworkTitle;
        }

        responseProblemDetails.Status = httpStatusCode;
        responseProblemDetails.Instance ??= httpContext.Request.Path;
        responseProblemDetails.Extensions["success"] = false;
        responseProblemDetails.Extensions.TryAdd("statusResultado", resultStatus.ToString());
        responseProblemDetails.Extensions.TryAdd("errors", Array.Empty<string>());
    }

    // The route template, never the raw path, keeps the log field low in cardinality. Inside the exception handler the
    // endpoint was already taken off the context and only the exception feature still has it.
    public static string? ReadRouteTemplate(HttpContext httpContext) =>
        ((httpContext.GetEndpoint() ?? httpContext.Features.Get<IExceptionHandlerFeature>()?.Endpoint) as RouteEndpoint)?.RoutePattern.RawText;

    private static string DescribeDefaultErrorCode(ResultStatus resultStatus) => resultStatus switch
    {
        ResultStatus.InvalidInput => "invalid-input",
        ResultStatus.Unauthorized => "unauthorized",
        ResultStatus.Forbidden => "forbidden",
        ResultStatus.NotFound => "not-found",
        ResultStatus.Conflict => "conflict",
        ResultStatus.BusinessRuleViolated => "business-rule-violated",
        ResultStatus.ServiceUnavailable => "service-unavailable",
        _ => "internal-error"
    };

    private static string DescribeDefaultTitle(ResultStatus resultStatus) => resultStatus switch
    {
        ResultStatus.InvalidInput => "Dados inválidos",
        ResultStatus.Unauthorized => "Não autenticado",
        ResultStatus.Forbidden => "Acesso negado",
        ResultStatus.NotFound => "Não encontrado",
        ResultStatus.Conflict => "Conflito",
        ResultStatus.BusinessRuleViolated => "Regra de negócio violada",
        ResultStatus.ServiceUnavailable => "Serviço indisponível",
        _ => "Erro interno"
    };
}
