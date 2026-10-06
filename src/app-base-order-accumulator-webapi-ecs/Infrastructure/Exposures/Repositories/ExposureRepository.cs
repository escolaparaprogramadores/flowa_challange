using Base.OrderAccumulator.Domain.Exposures;
using Dapper;

namespace Base.OrderAccumulator.Infrastructure.Persistence;

public sealed class ExposureRepository(PostgresUnitOfWork orderDatabaseUnitOfWork) : IExposureRepository
{
    // The limit is enforced here, in the database, not in memory: the UPDATE only moves the exposure if the
    // new value fits. Two orders on the same symbol at the same time compete for the row lock, and the
    // second one re-evaluates the condition on the value the first one just stored.
    private const string MoveExposureSql = """
        UPDATE exposures
        SET exposure = exposure + @Delta
        WHERE symbol = @Symbol AND abs(exposure + @Delta) <= @Limit
        """;

    private const string SymbolExistsSql = "SELECT count(*) FROM exposures WHERE symbol = @Symbol";

    // Zeroes instead of deleting: without the symbol row, moving the exposure throws
    // (TryMoveSymbolExposureWithinLimitAsync).
    private const string ZeroSymbolExposuresSql = "UPDATE exposures SET exposure = 0 WHERE symbol = ANY(@Symbols)";

    public async Task<bool> TryMoveSymbolExposureWithinLimitAsync(
        string orderSymbol, decimal exposureDelta, decimal exposureLimit, CancellationToken cancellationToken = default)
    {
        var orderDatabaseConnection = await orderDatabaseUnitOfWork.GetOpenConnectionAsync(cancellationToken);
        var exposureMoveParameters = new { Symbol = orderSymbol, Delta = exposureDelta, Limit = exposureLimit };

        var movedExposureRows = await orderDatabaseConnection.ExecuteAsync(new CommandDefinition(
            MoveExposureSql, exposureMoveParameters, orderDatabaseUnitOfWork.CurrentTransaction, cancellationToken: cancellationToken));
        if (movedExposureRows == 1)
            return true;

        var exposureRowsOfSymbol = await orderDatabaseConnection.ExecuteScalarAsync<long>(new CommandDefinition(
            SymbolExistsSql, exposureMoveParameters, orderDatabaseUnitOfWork.CurrentTransaction, cancellationToken: cancellationToken));
        if (exposureRowsOfSymbol == 0)
            throw new InvalidOperationException(
                $"The symbol {orderSymbol} has no exposure row. The database migration was not applied.");

        return false;
    }

    public async Task ZeroSymbolExposuresAsync(IReadOnlyList<string> orderSymbols, CancellationToken cancellationToken = default)
    {
        var orderDatabaseConnection = await orderDatabaseUnitOfWork.GetOpenConnectionAsync(cancellationToken);
        await orderDatabaseConnection.ExecuteAsync(new CommandDefinition(
            ZeroSymbolExposuresSql, new { Symbols = orderSymbols.ToArray() }, orderDatabaseUnitOfWork.CurrentTransaction, cancellationToken: cancellationToken));
    }
}
