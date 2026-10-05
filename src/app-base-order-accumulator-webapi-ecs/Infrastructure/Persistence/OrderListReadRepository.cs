using System.Data;
using Base.OrderAccumulator.Application.Orders.ListOrders;
using Dapper;
using Npgsql;

namespace Base.OrderAccumulator.Infrastructure.Persistence;

// The order list of the screen (GET /api/orders).
public sealed class OrderListReadRepository(NpgsqlDataSource orderDatabaseDataSource) : IOrderListReadRepository
{
    public const int OrdersPerPage = 10;

    private const string CountStoredOrdersSql = "SELECT count(*) FROM orders";

    // Uses the orders_received_at_id_idx index from Schema.sql; the id breaks ties between orders of the same instant.
    private const string SelectStoredOrderPageSql = """
        SELECT received_at AS ReceivedAt, accepted AS Accepted, symbol AS Symbol, side AS Side,
               quantity AS Quantity, price AS Price, order_id AS OrderId, cl_ord_id AS ClOrdId
        FROM orders
        ORDER BY received_at DESC, id DESC
        LIMIT @OrdersPerPage OFFSET @SkippedOrders
        """;

    // Total and page come from the same database snapshot, so the total never counts an order the page did not see.
    public async Task<OrderListPage> ReadStoredOrderPageAsync(int pageNumber, CancellationToken cancellationToken = default)
    {
        await using var orderDatabaseConnection = await orderDatabaseDataSource.OpenConnectionAsync(cancellationToken);
        await using var orderPageTransaction = await orderDatabaseConnection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);

        var totalStoredOrders = await orderDatabaseConnection.ExecuteScalarAsync<long>(new CommandDefinition(
            CountStoredOrdersSql, transaction: orderPageTransaction, cancellationToken: cancellationToken));
        var storedOrdersOfPage = await orderDatabaseConnection.QueryAsync<OrderListItem>(new CommandDefinition(
            SelectStoredOrderPageSql,
            new { OrdersPerPage, SkippedOrders = (pageNumber - 1) * OrdersPerPage },
            orderPageTransaction,
            cancellationToken: cancellationToken));

        await orderPageTransaction.CommitAsync(cancellationToken);
        return new OrderListPage(totalStoredOrders, storedOrdersOfPage.ToList());
    }
}
