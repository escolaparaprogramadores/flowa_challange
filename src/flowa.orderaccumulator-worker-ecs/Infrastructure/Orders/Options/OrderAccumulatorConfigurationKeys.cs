namespace Flowa.OrderAccumulator.Infrastructure.Orders.Options;

internal static class OrderAccumulatorConfigurationKeys
{
    public const string OrderDatabaseConnectionStringName = "Flowa";
    public const string FixAcceptorPort = "Fix:AcceptorPort";
    public const string FixAcceptorBindHost = "Fix:AcceptorBindHost";
    public const string OrderDecisionTimeoutSeconds = "Orders:DecisionTimeoutSeconds";
}
