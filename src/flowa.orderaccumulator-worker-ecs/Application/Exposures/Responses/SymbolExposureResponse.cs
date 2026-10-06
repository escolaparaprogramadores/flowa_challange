using Flowa.OrderAccumulator.Domain.Exposures.ValueObjects;

namespace Flowa.OrderAccumulator.Application.Exposures.Responses;

public sealed record SymbolExposureResponse(string Symbol, decimal Exposure, decimal Remaining)
{
    public static SymbolExposureResponse MapFromSymbolExposure(SymbolExposure symbolExposure)
    {
        ArgumentNullException.ThrowIfNull(symbolExposure);
        return new SymbolExposureResponse(symbolExposure.Symbol, symbolExposure.Exposure, symbolExposure.RemainingExposureCapacity);
    }
}
