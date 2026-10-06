using Flowa.OrderGenerator.Commons.Responses;

namespace Flowa.OrderGenerator.Entrypoint.ErrorHandling;

public static class ResultStatusHttpMapper
{
    public static int ConvertToHttpStatusCode(ResultStatus resultStatus) => resultStatus switch
    {
        ResultStatus.Ok => StatusCodes.Status200OK,
        ResultStatus.Created => StatusCodes.Status201Created,
        ResultStatus.Accepted => StatusCodes.Status202Accepted,
        ResultStatus.InvalidInput => StatusCodes.Status400BadRequest,
        ResultStatus.Unauthorized => StatusCodes.Status401Unauthorized,
        ResultStatus.Forbidden => StatusCodes.Status403Forbidden,
        ResultStatus.NotFound => StatusCodes.Status404NotFound,
        ResultStatus.Conflict => StatusCodes.Status409Conflict,
        ResultStatus.BusinessRuleViolated => StatusCodes.Status422UnprocessableEntity,
        ResultStatus.ServiceUnavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError
    };

    public static ResultStatus ConvertToResultStatus(int httpStatusCode) => httpStatusCode switch
    {
        StatusCodes.Status401Unauthorized => ResultStatus.Unauthorized,
        StatusCodes.Status403Forbidden => ResultStatus.Forbidden,
        StatusCodes.Status404NotFound => ResultStatus.NotFound,
        StatusCodes.Status409Conflict => ResultStatus.Conflict,
        StatusCodes.Status422UnprocessableEntity => ResultStatus.BusinessRuleViolated,
        StatusCodes.Status503ServiceUnavailable => ResultStatus.ServiceUnavailable,
        >= 400 and < 500 => ResultStatus.InvalidInput,
        _ => ResultStatus.InternalError
    };
}
