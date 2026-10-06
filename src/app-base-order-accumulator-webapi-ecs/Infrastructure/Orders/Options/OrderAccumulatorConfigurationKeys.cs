namespace Base.OrderAccumulator.Infrastructure.Orders.Options;

public static class OrderAccumulatorConfigurationKeys
{
    public const string OrderDatabaseConnectionStringName = "Flowa";
    public const string FixAcceptorPort = "Fix:AcceptorPort";
    public const string FixAcceptorBindHost = "Fix:AcceptorBindHost";
    public const string DatadogEnvironment = "DD_ENV";
    public const string DatadogService = "DD_SERVICE";
    public const string DatadogVersion = "DD_VERSION";
}
