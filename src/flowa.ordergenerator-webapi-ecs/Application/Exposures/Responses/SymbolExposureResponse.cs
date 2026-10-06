using Flowa.OrderGenerator.Domain.Exposures.ValueObjects;

namespace Flowa.OrderGenerator.Application.Exposures.Responses;

public sealed record SymbolExposureResponse(string Symbol, decimal Exposure, decimal Remaining)
{
    public static SymbolExposureResponse MapFromSymbolExposure(SymbolExposure symbolExposure)
    {
        ArgumentNullException.ThrowIfNull(symbolExposure);
        return new SymbolExposureResponse(symbolExposure.Symbol, symbolExposure.Exposure, symbolExposure.RemainingExposureCapacity);
    }
}
