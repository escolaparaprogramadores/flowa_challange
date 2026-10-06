using Flowa.Commons.Database;
using Flowa.OrderAccumulator.Domain.Orders.Entities;
using Flowa.OrderAccumulator.Domain.Orders.Interfaces;

namespace Flowa.OrderAccumulator.Infrastructure.Orders.Repositories;

public sealed class OrderRepository : IOrderRepository
{
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

    private readonly IDatabase orderDatabase;

    public OrderRepository(IDatabase orderDatabase)
    {
        this.orderDatabase = orderDatabase ?? throw new ArgumentNullException(nameof(orderDatabase));
    }

    public async Task<Order?> FindOrderByClOrdIdAsync(string clOrdId, CancellationToken cancellationToken = default)
    {
        var storedOrder = await orderDatabase.QuerySingleRecordAsync<StoredOrderRow>(SelectOrderSql, new { ClOrdId = clOrdId }, cancellationToken);
        if (storedOrder is null)
            return null;

        return Order.RestoreOrder(
            storedOrder.ClOrdId, storedOrder.OrderId, storedOrder.ExecId, storedOrder.Symbol, storedOrder.Side[0],
            storedOrder.Quantity, storedOrder.Price, storedOrder.Accepted, storedOrder.RejectReason);
    }

    public async Task<bool> TryAddOrderAsync(Order answeredOrder, CancellationToken cancellationToken = default)
    {
        var insertedOrders = await orderDatabase.ExecuteSqlCommandAsync(InsertOrderSql, ToOrderInsertParameters(answeredOrder), cancellationToken);
        return insertedOrders == 1;
    }

    public async Task DeleteAllOrdersAsync(CancellationToken cancellationToken = default) =>
        await orderDatabase.ExecuteSqlCommandAsync(DeleteAllStoredOrdersSql, null, cancellationToken);

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
