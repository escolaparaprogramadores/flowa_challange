using Base.OrderAccumulator.Domain.Exposures.ValueObjects;

namespace Base.OrderAccumulator.Application.Exposures.Interfaces;

public interface ISymbolExposureReadRepository
{
    Task<IReadOnlyList<SymbolExposure>> GetSymbolExposuresAsync(CancellationToken cancellationToken = default);
}
