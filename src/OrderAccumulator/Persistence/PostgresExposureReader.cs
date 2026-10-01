using Dapper;
using Flowa.Shared;
using Npgsql;
using OrderAccumulator.Exposure;

namespace OrderAccumulator.Persistence;

public sealed class PostgresExposureReader(NpgsqlDataSource dataSource) : IExposureReader
{
    private const string SelectExposuresSql = """
        SELECT symbol AS Symbol, exposure AS Exposure
        FROM exposures
        WHERE symbol = ANY(@Symbols)
        """;

    public async Task<IReadOnlyList<SymbolExposure>> GetSymbolExposuresAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var storedExposureRows = await connection.QueryAsync<StoredExposureRow>(new CommandDefinition(
            SelectExposuresSql, new { Symbols = OrderRules.AllowedOrderSymbols.ToArray() }, cancellationToken: cancellationToken));

        var exposureBySymbol = storedExposureRows.ToDictionary(exposureRow => exposureRow.Symbol, exposureRow => exposureRow.Exposure);
        return OrderRules.AllowedOrderSymbols
            .Select(symbol => new SymbolExposure(symbol, exposureBySymbol.TryGetValue(symbol, out var storedExposure)
                ? storedExposure
                : throw new InvalidOperationException(
                    $"O símbolo {symbol} não tem linha de exposição. A migração do banco não foi aplicada.")))
            .ToList();
    }

    private sealed record StoredExposureRow(string Symbol, decimal Exposure);
}
