namespace Base.OrderAccumulator.Domain.Exposures.ValueObjects;

public sealed record SymbolExposure(string Symbol, decimal Exposure)
{
    public decimal RemainingExposureCapacity => ExposureLimitPolicy.CalculateRemainingExposureCapacity(Exposure);
}
