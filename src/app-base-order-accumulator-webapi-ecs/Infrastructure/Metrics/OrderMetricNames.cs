namespace Base.OrderAccumulator.Infrastructure.Metrics;

// Nomes combinados com o painel do Datadog (observabilidade/datadog/). Mudar aqui apaga o gráfico.
public static class OrderMetricNames
{
    public const string AcceptedOrders = "flowa.ordens.aceitas";
    public const string RejectedOrders = "flowa.ordens.rejeitadas";
    public const string SymbolExposure = "flowa.exposicao";
}
