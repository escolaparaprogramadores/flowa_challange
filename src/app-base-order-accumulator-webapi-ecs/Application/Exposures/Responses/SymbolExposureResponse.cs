using Base.OrderAccumulator.Domain.Exposures.ValueObjects;

namespace Base.OrderAccumulator.Application.Exposures.Responses;

public sealed record SymbolExposureResponse(string Symbol, decimal Exposure, decimal Remaining)
{
    public static SymbolExposureResponse MapFromSymbolExposure(SymbolExposure symbolExposure)
    {
        ArgumentNullException.ThrowIfNull(symbolExposure);
        return new SymbolExposureResponse(symbolExposure.Symbol, symbolExposure.Exposure, symbolExposure.RemainingExposureCapacity);
    }
}
