namespace Flowa.OrderGenerator.Domain.Exposures.ValueObjects;

public sealed record SymbolExposure
{
    public string Symbol { get; }
    public decimal Exposure { get; }
    public decimal RemainingExposureCapacity => ExposureLimitPolicy.CalculateRemainingExposureCapacity(Exposure);

    public SymbolExposure(string symbol, decimal exposure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        Symbol = symbol;
        Exposure = exposure;
    }

    public static IReadOnlyList<SymbolExposure> CreateZeroSymbolExposures() =>
        ExposureLimitPolicy.ExposureSymbols.Select(exposureSymbol => new SymbolExposure(exposureSymbol, 0m)).ToList();
}
