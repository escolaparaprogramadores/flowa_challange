using Base.OrderAccumulator.Domain.Exposures;

namespace Base.OrderAccumulator.Application.Exposures.GetExposures;

// The exposure of the three symbols, read from the database for the screen.
public sealed class GetExposuresUseCase(ISymbolExposureReadRepository symbolExposureReadRepository)
{
    public Task<IReadOnlyList<SymbolExposure>> GetExposuresAsync(CancellationToken cancellationToken = default) =>
        symbolExposureReadRepository.GetSymbolExposuresAsync(cancellationToken);
}
