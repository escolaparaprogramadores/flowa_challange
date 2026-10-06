using Base.OrderAccumulator.Domain.Exposures.ValueObjects;

namespace Base.OrderAccumulator.Infrastructure.Orders.Adapters;

public static class OrderMetricNames
{
    public const string AcceptedOrders = "flowa.ordens.aceitas";
    public const string RejectedOrders = "flowa.ordens.rejeitadas";
    public const string SymbolExposure = "flowa.exposicao";
}
