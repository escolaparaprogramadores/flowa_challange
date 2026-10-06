using Flowa.OrderGenerator.Application.ErrorHandling;
using Flowa.Commons.Responses;
using Microsoft.AspNetCore.Diagnostics;

namespace Flowa.OrderGenerator.Entrypoint.ErrorHandling;

public sealed class OrderGeneratorExceptionHandler : IExceptionHandler
{
    public const string InvalidRequestMessage = "Dados inválidos";
    public const string InvalidRequestErrorCode = "invalid-input";

    private readonly IProblemDetailsService _problemDetailsService;
    private readonly DataMessageHttpResponseConverter _dataMessageHttpResponseConverter;

    public OrderGeneratorExceptionHandler(IProblemDetailsService problemDetailsService, DataMessageHttpResponseConverter dataMessageHttpResponseConverter)
    {
        _problemDetailsService = problemDetailsService ?? throw new ArgumentNullException(nameof(problemDetailsService));
        _dataMessageHttpResponseConverter = dataMessageHttpResponseConverter ?? throw new ArgumentNullException(nameof(dataMessageHttpResponseConverter));
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var errorProblemDetails = exception is BadHttpRequestException
            ? ApiProblemDetailsExtensions.BuildErrorProblemDetails(ResultStatus.InvalidInput, InvalidRequestErrorCode, InvalidRequestMessage, [])
            : ApiProblemDetailsExtensions.BuildErrorProblemDetails(
                ResultStatus.InternalError, UseCaseFailureDataMessageMapper.UnexpectedErrorCode, UseCaseFailureDataMessageMapper.UnexpectedErrorMessage, []);

        _dataMessageHttpResponseConverter.WriteHttpErrorLogUnderItsTrace(httpContext, errorProblemDetails, exception, null);

        httpContext.Response.StatusCode = errorProblemDetails.Status!.Value;
        await _problemDetailsService.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = errorProblemDetails
        });
        return true;
    }
}
