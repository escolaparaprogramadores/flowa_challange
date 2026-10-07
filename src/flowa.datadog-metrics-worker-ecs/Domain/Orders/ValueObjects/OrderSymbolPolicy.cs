namespace Flowa.DatadogMetrics.Domain.Orders.ValueObjects;

public static class OrderSymbolPolicy
{
    public static readonly IReadOnlyList<string> AllowedOrderSymbols = ["PETR4", "VALE3", "VIIA4"];

    public static bool IsAllowedOrderSymbol(string? orderSymbol) => orderSymbol is not null && AllowedOrderSymbols.Contains(orderSymbol);
}
