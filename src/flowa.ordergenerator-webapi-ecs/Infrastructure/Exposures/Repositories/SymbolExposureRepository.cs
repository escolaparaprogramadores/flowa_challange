using Flowa.Commons.Database;
using Flowa.OrderGenerator.Application.Exposures.Interfaces;
using Flowa.OrderGenerator.Domain.Exposures.ValueObjects;

namespace Flowa.OrderGenerator.Infrastructure.Exposures.Repositories;

internal sealed class SymbolExposureRepository : ISymbolExposureRepository
{
    private const string ExposureTableExistsSql = "SELECT to_regclass('exposures') IS NOT NULL";

    private const string SelectSymbolExposuresSql = """
        SELECT symbol AS Symbol, exposure AS Exposure
        FROM exposures
        WHERE symbol = ANY(@Symbols)
        """;

    private const string ZeroSymbolExposuresSql = "UPDATE exposures SET exposure = 0 WHERE symbol = ANY(@Symbols)";

    private readonly IDatabase _orderDatabase;

    public SymbolExposureRepository(IDatabase orderDatabase)
    {
        _orderDatabase = orderDatabase ?? throw new ArgumentNullException(nameof(orderDatabase));
    }

    public async Task<bool> ExposureTableExistsAsync(CancellationToken cancellationToken) =>
        await _orderDatabase.QueryScalarAsync<bool>(ExposureTableExistsSql, null, cancellationToken);

    public async Task<IReadOnlyList<SymbolExposure>> GetSymbolExposuresAsync(CancellationToken cancellationToken)
    {
        var storedExposureRows = await _orderDatabase.QueryRecordsAsync<StoredExposureRow>(
            SelectSymbolExposuresSql, new { Symbols = ExposureLimitPolicy.ExposureSymbols.ToArray() }, cancellationToken);

        var storedExposureBySymbol = storedExposureRows.ToDictionary(storedExposureRow => storedExposureRow.Symbol, storedExposureRow => storedExposureRow.Exposure);
        return ExposureLimitPolicy.ExposureSymbols
            .Select(exposureSymbol => new SymbolExposure(exposureSymbol, storedExposureBySymbol.TryGetValue(exposureSymbol, out var storedExposure)
                ? storedExposure
                : throw new InvalidOperationException(
                    $"The symbol {exposureSymbol} has no exposure row. The database migration was not applied.")))
            .ToList();
    }

    public async Task ZeroSymbolExposuresAsync(CancellationToken cancellationToken) =>
        await _orderDatabase.ExecuteSqlCommandAsync(ZeroSymbolExposuresSql, new { Symbols = ExposureLimitPolicy.ExposureSymbols.ToArray() }, cancellationToken);

    private sealed record StoredExposureRow(string Symbol, decimal Exposure);
}
