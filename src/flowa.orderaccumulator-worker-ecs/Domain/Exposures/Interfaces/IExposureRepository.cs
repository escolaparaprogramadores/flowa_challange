namespace Flowa.OrderAccumulator.Domain.Exposures.Interfaces;

public interface IExposureRepository
{
    Task<bool> TryMoveSymbolExposureWithinLimitAsync(
        string orderSymbol, decimal exposureDelta, decimal exposureLimit, CancellationToken cancellationToken = default);

    Task ZeroSymbolExposuresAsync(IReadOnlyList<string> orderSymbols, CancellationToken cancellationToken = default);
}
