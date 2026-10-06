namespace Flowa.OrderGenerator.Domain.Exposures.ValueObjects;

public static class ExposureLimitPolicy
{
    public const decimal PerSymbol = 100_000_000m;

    public static readonly IReadOnlyList<string> ExposureSymbols = ["PETR4", "VALE3", "VIIA4"];

    public static decimal CalculateRemainingExposureCapacity(decimal symbolExposure) => PerSymbol - Math.Abs(symbolExposure);
}
