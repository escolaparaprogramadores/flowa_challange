using Flowa.Commons.Database;
using Flowa.DatadogMetrics.Application.Exposures.Interfaces;
using Flowa.DatadogMetrics.Domain.Exposures.ValueObjects;
using Flowa.DatadogMetrics.Domain.Orders.ValueObjects;

namespace Flowa.DatadogMetrics.Infrastructure.Exposures.Repositories;

internal sealed class SymbolExposureReadRepository : ISymbolExposureReadRepository
{
    private const string SelectExposuresSql = """
        SELECT symbol AS Symbol, exposure AS Exposure
        FROM exposures
        WHERE symbol = ANY(@Symbols)
        """;

    private readonly IDatabase flowaDatabase;

    public SymbolExposureReadRepository(IDatabase flowaDatabase)
    {
        this.flowaDatabase = flowaDatabase ?? throw new ArgumentNullException(nameof(flowaDatabase));
    }

    public async Task<IReadOnlyList<SymbolExposure>> GetSymbolExposuresAsync(CancellationToken cancellationToken = default)
    {
        var storedExposureRows = await flowaDatabase.QueryRecordsAsync<StoredExposureRow>(
            SelectExposuresSql, new { Symbols = OrderSymbolPolicy.AllowedOrderSymbols.ToArray() }, cancellationToken);

        var exposureBySymbol = storedExposureRows.ToDictionary(storedExposureRow => storedExposureRow.Symbol, storedExposureRow => storedExposureRow.Exposure);
        return OrderSymbolPolicy.AllowedOrderSymbols
            .Select(allowedSymbol => new SymbolExposure(allowedSymbol, exposureBySymbol.TryGetValue(allowedSymbol, out var storedExposure)
                ? storedExposure
                : throw new InvalidOperationException($"The symbol {allowedSymbol} has no exposure row yet.")))
            .ToList();
    }

    private sealed record StoredExposureRow(string Symbol, decimal Exposure);
}
