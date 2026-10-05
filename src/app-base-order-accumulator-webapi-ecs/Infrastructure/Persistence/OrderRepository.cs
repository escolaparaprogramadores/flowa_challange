using Base.OrderAccumulator.Domain.Orders;
using Dapper;

namespace Base.OrderAccumulator.Infrastructure.Persistence;

public sealed class OrderRepository(PostgresUnitOfWork orderDatabaseUnitOfWork) : IOrderRepository
{
    // If the ClOrdID already exists, nothing is written: the caller rolls back and reads the original answer.
    private const string InsertOrderSql = """
        INSERT INTO orders (cl_ord_id, order_id, exec_id, symbol, side, quantity, price, accepted, reject_reason)
        VALUES (@ClOrdId, @OrderId, @ExecId, @Symbol, @Side, @Quantity, @Price, @Accepted, @RejectReason)
        ON CONFLICT (cl_ord_id) DO NOTHING
        """;

    private const string SelectOrderSql = """
        SELECT cl_ord_id AS ClOrdId, order_id AS OrderId, exec_id AS ExecId, symbol AS Symbol, side AS Side,
               quantity AS Quantity, price AS Price, accepted AS Accepted, reject_reason AS RejectReason
        FROM orders
        WHERE cl_ord_id = @ClOrdId
        """;

    private const string DeleteAllStoredOrdersSql = "DELETE FROM orders";

    public async Task<Order?> FindOrderByClOrdIdAsync(string clOrdId, CancellationToken cancellationToken = default)
    {
        var orderDatabaseConnection = await orderDatabaseUnitOfWork.GetOpenConnectionAsync(cancellationToken);
        var storedOrder = await orderDatabaseConnection.QuerySingleOrDefaultAsync<StoredOrderRow>(new CommandDefinition(
            SelectOrderSql, new { ClOrdId = clOrdId }, orderDatabaseUnitOfWork.CurrentTransaction, cancellationToken: cancellationToken));
        if (storedOrder is null)
            return null;

        return Order.RestoreOrder(
            storedOrder.ClOrdId, storedOrder.OrderId, storedOrder.ExecId, storedOrder.Symbol, storedOrder.Side[0],
            storedOrder.Quantity, storedOrder.Price, storedOrder.Accepted, storedOrder.RejectReason);
    }

    public async Task<bool> TryAddOrderAsync(Order answeredOrder, CancellationToken cancellationToken = default)
    {
        var orderDatabaseConnection = await orderDatabaseUnitOfWork.GetOpenConnectionAsync(cancellationToken);
        var insertedOrders = await orderDatabaseConnection.ExecuteAsync(new CommandDefinition(
            InsertOrderSql, ToOrderInsertParameters(answeredOrder), orderDatabaseUnitOfWork.CurrentTransaction, cancellationToken: cancellationToken));
        return insertedOrders == 1;
    }

    public async Task DeleteAllOrdersAsync(CancellationToken cancellationToken = default)
    {
        var orderDatabaseConnection = await orderDatabaseUnitOfWork.GetOpenConnectionAsync(cancellationToken);
        await orderDatabaseConnection.ExecuteAsync(new CommandDefinition(
            DeleteAllStoredOrdersSql, transaction: orderDatabaseUnitOfWork.CurrentTransaction, cancellationToken: cancellationToken));
    }

    private static object ToOrderInsertParameters(Order answeredOrder) => new
    {
        answeredOrder.ClOrdId,
        answeredOrder.OrderId,
        answeredOrder.ExecId,
        answeredOrder.Symbol,
        Side = answeredOrder.Side.ToString(),
        answeredOrder.Quantity,
        answeredOrder.Price,
        answeredOrder.Accepted,
        answeredOrder.RejectReason
    };

    private sealed record StoredOrderRow(
        string ClOrdId,
        string OrderId,
        string ExecId,
        string? Symbol,
        string Side,
        decimal Quantity,
        decimal Price,
        bool Accepted,
        string? RejectReason);
}
