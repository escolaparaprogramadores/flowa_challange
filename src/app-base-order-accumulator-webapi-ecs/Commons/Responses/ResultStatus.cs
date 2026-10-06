namespace Base.OrderAccumulator.Commons.Responses;

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
