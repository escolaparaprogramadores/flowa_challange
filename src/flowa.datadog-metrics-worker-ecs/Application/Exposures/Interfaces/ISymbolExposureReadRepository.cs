using Flowa.DatadogMetrics.Domain.Exposures.ValueObjects;

namespace Flowa.DatadogMetrics.Application.Exposures.Interfaces;

public interface ISymbolExposureReadRepository
{
    Task<IReadOnlyList<SymbolExposure>> GetSymbolExposuresAsync(CancellationToken cancellationToken = default);
}
