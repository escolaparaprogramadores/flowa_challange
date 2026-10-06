namespace Flowa.OrderGenerator.Commons.Responses;

public enum ResultStatus
{
    Ok,
    Created,
    Accepted,
    InvalidInput,
    Unauthorized,
    Forbidden,
    NotFound,
    Conflict,
    BusinessRuleViolated,
    ServiceUnavailable,
    InternalError
}
