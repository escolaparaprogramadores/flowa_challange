namespace Base.OrderAccumulator.Domain.Exposures;

public interface IExposureRepository
{
    // Moves the exposure only if the new value stays within the limit, in one atomic step of the
    // database, so two simultaneous orders never both pass. False means the limit would be exceeded.
    Task<bool> TryMoveSymbolExposureWithinLimitAsync(
        string orderSymbol, decimal exposureDelta, decimal exposureLimit, CancellationToken cancellationToken = default);

    Task ZeroSymbolExposuresAsync(IReadOnlyList<string> orderSymbols, CancellationToken cancellationToken = default);
}
