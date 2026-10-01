using Dapper;
using Flowa.Shared;
using Npgsql;
using OrderAccumulator.Exposure;

namespace OrderAccumulator.Persistence;

public sealed class PostgresExposureReader(NpgsqlDataSource orderDatabaseDataSource) : IExposureReader
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
            SelectExposuresSql, new { Symbols = OrderRules.AllowedOrderSymbols.ToArray() }, cancellationToken: cancellationToken));

        var exposureBySymbol = storedExposureRows.ToDictionary(exposureRow => exposureRow.Symbol, exposureRow => exposureRow.Exposure);
        return OrderRules.AllowedOrderSymbols
            .Select(allowedSymbol => new SymbolExposure(allowedSymbol, exposureBySymbol.TryGetValue(allowedSymbol, out var storedExposure)
                ? storedExposure
                : throw new InvalidOperationException(
                    $"O símbolo {allowedSymbol} não tem linha de exposição. A migração do banco não foi aplicada.")))
            .ToList();
    }

    private sealed record StoredExposureRow(string Symbol, decimal Exposure);
}
