using Flowa.OrderAccumulator.Application.Exposures.Interfaces;
using Flowa.Commons.Database;
using Flowa.OrderAccumulator.Domain.Exposures.ValueObjects;
using Flowa.OrderAccumulator.Domain.Orders.ValueObjects;

namespace Flowa.OrderAccumulator.Infrastructure.Exposures.Repositories;

public sealed class SymbolExposureReadRepository : ISymbolExposureReadRepository
{
    private const string SelectExposuresSql = """
        SELECT symbol AS Symbol, exposure AS Exposure
        FROM exposures
        WHERE symbol = ANY(@Symbols)
        """;

    private readonly IDatabase orderDatabase;

    public SymbolExposureReadRepository(IDatabase orderDatabase)
    {
        this.orderDatabase = orderDatabase ?? throw new ArgumentNullException(nameof(orderDatabase));
    }

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
