namespace Base.OrderAccumulator.Commons;

// Names of the keys the app reads from configuration; the infra (infra/servicos.tf) uses the same names.
public static class OrderAccumulatorConfigurationKeys
{
    public const string OrderDatabaseConnectionStringName = "Flowa";
    public const string FixAcceptorPort = "Fix:AcceptorPort";
    public const string FixAcceptorBindHost = "Fix:AcceptorBindHost";
    public const string DatadogEnvironment = "DD_ENV";
    public const string DatadogService = "DD_SERVICE";
    public const string DatadogVersion = "DD_VERSION";
}
