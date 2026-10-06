using Flowa.OrderGenerator.Domain.Exposures.ValueObjects;

namespace Flowa.OrderGenerator.Application.Exposures.Responses;

public sealed record ExposuresResponse(decimal Limit, IReadOnlyList<SymbolExposureResponse> Exposures)
{
    public static ExposuresResponse MapFromSymbolExposures(IReadOnlyList<SymbolExposure> symbolExposures)
    {
        ArgumentNullException.ThrowIfNull(symbolExposures);

        return new ExposuresResponse(
            ExposureLimitPolicy.PerSymbol,
            symbolExposures.Select(SymbolExposureResponse.MapFromSymbolExposure).ToList());
    }
}
