namespace Base.OrderAccumulator.Commons;

// Outcome of a use case, with no transport in it; only the Entrypoint turns it into an HTTP status (backend-datamessage.md).
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
