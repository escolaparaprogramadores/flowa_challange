namespace Flowa.DatadogMetrics.Domain.Exposures.ValueObjects;

public sealed record SymbolExposure
{
    public string Symbol { get; }
    public decimal Exposure { get; }

    public SymbolExposure(string symbol, decimal exposure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        Symbol = symbol;
        Exposure = exposure;
    }
}
