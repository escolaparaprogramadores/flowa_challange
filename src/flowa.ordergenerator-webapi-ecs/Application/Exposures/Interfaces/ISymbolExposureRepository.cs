using Flowa.OrderGenerator.Domain.Exposures.ValueObjects;

namespace Flowa.OrderGenerator.Application.Exposures.Interfaces;

public interface ISymbolExposureRepository
{
    Task<bool> ExposureTableExistsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<SymbolExposure>> GetSymbolExposuresAsync(CancellationToken cancellationToken);

    Task ZeroSymbolExposuresAsync(CancellationToken cancellationToken);
}
