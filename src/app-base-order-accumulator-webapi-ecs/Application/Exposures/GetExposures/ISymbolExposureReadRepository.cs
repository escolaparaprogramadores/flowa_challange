using Base.OrderAccumulator.Domain.Exposures;

namespace Base.OrderAccumulator.Application.Exposures.GetExposures;

public interface ISymbolExposureReadRepository
{
    // The three symbols, always in the order of OrderRules.AllowedOrderSymbols.
    Task<IReadOnlyList<SymbolExposure>> GetSymbolExposuresAsync(CancellationToken cancellationToken = default);
}
