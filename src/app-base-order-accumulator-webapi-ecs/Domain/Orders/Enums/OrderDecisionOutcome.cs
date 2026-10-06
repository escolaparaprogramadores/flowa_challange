namespace Base.OrderAccumulator.Domain.Orders.Enums;

public enum OrderDecisionOutcome
{
    Accepted,
    RejectedForInvalidFields,
    RejectedOverExposureLimit
}
