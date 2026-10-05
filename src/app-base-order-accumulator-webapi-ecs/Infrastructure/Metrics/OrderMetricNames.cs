namespace Base.OrderAccumulator.Infrastructure.Metrics;

// Names agreed with the Datadog dashboard (observabilidade/datadog/). Changing them here erases the chart.
public static class OrderMetricNames
{
    public const string AcceptedOrders = "flowa.ordens.aceitas";
    public const string RejectedOrders = "flowa.ordens.rejeitadas";
    public const string SymbolExposure = "flowa.exposicao";
}
