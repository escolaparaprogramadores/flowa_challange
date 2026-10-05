using Base.OrderAccumulator.Application.Exposures.GetExposures;
using Base.OrderAccumulator.Domain.Exposures;
using Base.OrderAccumulator.Domain.Orders;
using Dapper;
using Npgsql;

namespace Base.OrderAccumulator.Infrastructure.Persistence;

public sealed class SymbolExposureReadRepository(NpgsqlDataSource orderDatabaseDataSource) : ISymbolExposureReadRepository
{
    private const string SelectExposuresSql = """
        SELECT symbol AS Symbol, exposure AS Exposure
        FROM exposures
        WHERE symbol = ANY(@Symbols)
        """;

    public async Task<IReadOnlyList<SymbolExposure>> GetSymbolExposuresAsync(CancellationToken cancellationToken = default)
    {
        await using var orderDatabaseConnection = await orderDatabaseDataSource.OpenConnectionAsync(cancellationToken);
        var storedExposureRows = await orderDatabaseConnection.QueryAsync<StoredExposureRow>(new CommandDefinition(
            SelectExposuresSql, new { Symbols = OrderFieldRule.AllowedOrderSymbols.ToArray() }, cancellationToken: cancellationToken));

        var exposureBySymbol = storedExposureRows.ToDictionary(exposureRow => exposureRow.Symbol, exposureRow => exposureRow.Exposure);
        return OrderFieldRule.AllowedOrderSymbols
            .Select(allowedSymbol => new SymbolExposure(allowedSymbol, exposureBySymbol.TryGetValue(allowedSymbol, out var storedExposure)
                ? storedExposure
                : throw new InvalidOperationException(
                    $"The symbol {allowedSymbol} has no exposure row. The database migration was not applied.")))
            .ToList();
    }

    private sealed record StoredExposureRow(string Symbol, decimal Exposure);
}
