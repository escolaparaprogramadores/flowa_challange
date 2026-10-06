using Base.OrderAccumulator.Application.Exposures.Interfaces;
using Base.OrderAccumulator.Commons.Database;
using Base.OrderAccumulator.Domain.Exposures.ValueObjects;
using Base.OrderAccumulator.Domain.Orders.ValueObjects;

namespace Base.OrderAccumulator.Infrastructure.Exposures.Repositories;

public sealed class SymbolExposureReadRepository(IDatabase orderDatabase) : ISymbolExposureReadRepository
{
    private const string SelectExposuresSql = """
        SELECT symbol AS Symbol, exposure AS Exposure
        FROM exposures
        WHERE symbol = ANY(@Symbols)
        """;

    public async Task<IReadOnlyList<SymbolExposure>> GetSymbolExposuresAsync(CancellationToken cancellationToken = default)
    {
        var storedExposureRows = await orderDatabase.QueryRecordsAsync<StoredExposureRow>(
            SelectExposuresSql, new { Symbols = OrderFieldPolicy.AllowedOrderSymbols.ToArray() }, cancellationToken);

        var exposureBySymbol = storedExposureRows.ToDictionary(exposureRow => exposureRow.Symbol, exposureRow => exposureRow.Exposure);
        return OrderFieldPolicy.AllowedOrderSymbols
            .Select(allowedSymbol => new SymbolExposure(allowedSymbol, exposureBySymbol.TryGetValue(allowedSymbol, out var storedExposure)
                ? storedExposure
                : throw new InvalidOperationException(
                    $"The symbol {allowedSymbol} has no exposure row. The database migration was not applied.")))
            .ToList();
    }

    private sealed record StoredExposureRow(string Symbol, decimal Exposure);
}
