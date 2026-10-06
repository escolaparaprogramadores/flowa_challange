using Flowa.Commons.Database;
using Flowa.OrderGenerator.Application.Orders.Interfaces;
using Flowa.OrderGenerator.Application.Orders.Responses;
using Flowa.OrderGenerator.Domain.Orders.ValueObjects;

namespace Flowa.OrderGenerator.Infrastructure.Orders.Repositories;

internal sealed class StoredOrderRepository : IStoredOrderRepository
{
    private const string OrderTableExistsSql = "SELECT to_regclass('orders') IS NOT NULL";

    private const string CountStoredOrdersSql = "SELECT count(*) FROM orders";

    private const string SelectStoredOrderPageSql = """
        SELECT received_at AS ReceivedAt, accepted AS Accepted, symbol AS Symbol, side AS Side,
               quantity AS Quantity, price AS Price, order_id AS OrderId, cl_ord_id AS ClOrdId, reject_reason AS RejectReason
        FROM orders
        ORDER BY received_at DESC, id DESC
        LIMIT @OrdersPerPage OFFSET @SkippedOrders
        """;

    private const string DeleteAllStoredOrdersSql = "DELETE FROM orders";

    private readonly IDatabase _orderDatabase;

    public StoredOrderRepository(IDatabase orderDatabase)
    {
        _orderDatabase = orderDatabase ?? throw new ArgumentNullException(nameof(orderDatabase));
    }

    public async Task<bool> OrderTableExistsAsync(CancellationToken cancellationToken) =>
        await _orderDatabase.QueryScalarAsync<bool>(OrderTableExistsSql, null, cancellationToken);

    public Task<StoredOrderPageResponse> ReadStoredOrderPageAsync(int orderListPageNumber, CancellationToken cancellationToken) =>
        _orderDatabase.ReadInRepeatableReadSnapshotAsync(async () =>
        {
            var totalStoredOrders = await _orderDatabase.QueryScalarAsync<long>(CountStoredOrdersSql, null, cancellationToken);
            var storedOrdersOfPage = await _orderDatabase.QueryRecordsAsync<StoredOrderResponse>(
                SelectStoredOrderPageSql,
                new { OrderListPagePolicy.OrdersPerPage, SkippedOrders = (orderListPageNumber - 1) * OrderListPagePolicy.OrdersPerPage },
                cancellationToken);
            return new StoredOrderPageResponse(totalStoredOrders, storedOrdersOfPage);
        }, cancellationToken);

    public async Task DeleteAllStoredOrdersAsync(CancellationToken cancellationToken) =>
        await _orderDatabase.ExecuteSqlCommandAsync(DeleteAllStoredOrdersSql, null, cancellationToken);
}
