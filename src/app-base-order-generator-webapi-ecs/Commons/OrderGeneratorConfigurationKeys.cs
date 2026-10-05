namespace Base.OrderGenerator.Commons;

// Names of the keys the app reads from configuration; the infra (infra/servicos.tf) uses the same names.
public static class OrderGeneratorConfigurationKeys
{
    public const string FixAcceptorHost = "Fix:AcceptorHost";
    public const string FixAcceptorPort = "Fix:AcceptorPort";
    public const string OrderAccumulatorBaseUrl = "OrderAccumulator:BaseUrl";
}
