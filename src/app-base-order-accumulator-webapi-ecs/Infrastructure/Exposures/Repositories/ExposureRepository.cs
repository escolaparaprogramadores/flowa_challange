using Base.OrderAccumulator.Commons.Database;
using Base.OrderAccumulator.Domain.Exposures.Interfaces;

namespace Base.OrderAccumulator.Infrastructure.Exposures.Repositories;

public sealed class ExposureRepository(IDatabase orderDatabase) : IExposureRepository
{
    private const string MoveExposureSql = """
        UPDATE exposures
        SET exposure = exposure + @Delta
        WHERE symbol = @Symbol AND abs(exposure + @Delta) <= @Limit
        """;

    private const string SymbolExistsSql = "SELECT count(*) FROM exposures WHERE symbol = @Symbol";

    private const string ZeroSymbolExposuresSql = "UPDATE exposures SET exposure = 0 WHERE symbol = ANY(@Symbols)";

    public async Task<bool> TryMoveSymbolExposureWithinLimitAsync(
        string orderSymbol, decimal exposureDelta, decimal exposureLimit, CancellationToken cancellationToken = default)
    {
        var exposureMoveParameters = new { Symbol = orderSymbol, Delta = exposureDelta, Limit = exposureLimit };

        var movedExposureRows = await orderDatabase.ExecuteSqlCommandAsync(MoveExposureSql, exposureMoveParameters, cancellationToken);
        if (movedExposureRows == 1)
            return true;

        var exposureRowsOfSymbol = await orderDatabase.QueryScalarAsync<long>(SymbolExistsSql, exposureMoveParameters, cancellationToken);
        if (exposureRowsOfSymbol == 0)
            throw new InvalidOperationException(
                $"The symbol {orderSymbol} has no exposure row. The database migration was not applied.");

        return false;
    }

    public async Task ZeroSymbolExposuresAsync(IReadOnlyList<string> orderSymbols, CancellationToken cancellationToken = default) =>
        await orderDatabase.ExecuteSqlCommandAsync(ZeroSymbolExposuresSql, new { Symbols = orderSymbols.ToArray() }, cancellationToken);
}
