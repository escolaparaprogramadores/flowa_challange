using Flowa.OrderAccumulator.Domain.Exposures.ValueObjects;

namespace Flowa.OrderAccumulator.Application.Exposures.Interfaces;

public interface ISymbolExposureReadRepository
{
    Task<IReadOnlyList<SymbolExposure>> GetSymbolExposuresAsync(CancellationToken cancellationToken = default);
}
