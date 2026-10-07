using Flowa.Commons.Database;
using Flowa.DatadogMetrics.Application.Orders.Interfaces;
using Flowa.DatadogMetrics.Domain.Orders.ValueObjects;

namespace Flowa.DatadogMetrics.Infrastructure.Orders.Repositories;

internal sealed class AnsweredOrderCountReadRepository : IAnsweredOrderCountReadRepository
{
    private const string SelectLastStoredOrderIdSql = "SELECT coalesce(max(id), 0) FROM orders";

    private const string SelectAnsweredOrderCountsSql = """
        SELECT symbol AS Symbol, side AS Side, accepted AS Accepted, count(*) AS OrderCount, max(id) AS LastOrderId
        FROM orders
        WHERE id > @LastCountedOrderId
        GROUP BY symbol, side, accepted
        """;

    private readonly IDatabase flowaDatabase;

    public AnsweredOrderCountReadRepository(IDatabase flowaDatabase)
    {
        this.flowaDatabase = flowaDatabase ?? throw new ArgumentNullException(nameof(flowaDatabase));
    }

    public async Task<long> GetLastStoredOrderIdAsync(CancellationToken cancellationToken = default) =>
        await flowaDatabase.QueryScalarAsync<long>(SelectLastStoredOrderIdSql, null, cancellationToken);

    public async Task<IReadOnlyList<AnsweredOrderCount>> GetAnsweredOrderCountsAfterAsync(long lastCountedOrderId, CancellationToken cancellationToken = default)
    {
        var answeredOrderCountRows = await flowaDatabase.QueryRecordsAsync<AnsweredOrderCountRow>(
            SelectAnsweredOrderCountsSql, new { LastCountedOrderId = lastCountedOrderId }, cancellationToken);

        return answeredOrderCountRows
            .Select(answeredOrderCountRow => new AnsweredOrderCount(
                answeredOrderCountRow.Symbol, answeredOrderCountRow.Side, answeredOrderCountRow.Accepted,
                answeredOrderCountRow.OrderCount, answeredOrderCountRow.LastOrderId))
            .ToList();
    }

    private sealed record AnsweredOrderCountRow(string? Symbol, string Side, bool Accepted, long OrderCount, long LastOrderId);
}
