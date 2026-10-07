using Flowa.Commons.Database;
using Flowa.OrderAccumulator.Domain.Exposures.Interfaces;

namespace Flowa.OrderAccumulator.Infrastructure.Exposures.Repositories;

public sealed class ExposureRepository : IExposureRepository
{
    private readonly IDatabase orderDatabase;

    private const string MoveExposureSql = """
        UPDATE exposures
        SET exposure = exposure + @Delta
        WHERE symbol = @Symbol AND abs(exposure + @Delta) <= @Limit
        """;

    private const string SymbolExistsSql = "SELECT count(*) FROM exposures WHERE symbol = @Symbol";

    public ExposureRepository(IDatabase orderDatabase)
    {
        this.orderDatabase = orderDatabase ?? throw new ArgumentNullException(nameof(orderDatabase));
    }

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
}
