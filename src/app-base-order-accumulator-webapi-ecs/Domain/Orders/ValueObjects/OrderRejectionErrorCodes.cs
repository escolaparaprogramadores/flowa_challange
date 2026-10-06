namespace Base.OrderAccumulator.Domain.Orders.ValueObjects;

public static class OrderRejectionErrorCodes
{
    public const string InvalidOrderFields = "invalid_order_fields";
    public const string ExposureLimitExceeded = "exposure_limit_exceeded";
}
