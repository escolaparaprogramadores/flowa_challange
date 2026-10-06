using Flowa.OrderGenerator.Commons.Responses;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Flowa.OrderGenerator.Entrypoint.ErrorHandling;

public static class ApiProblemDetailsExtensions
{
    public const string ProblemTypePrefix = "urn:base-investimentos:problem:";

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
