using Base.OrderAccumulator.Application.Orders.Interfaces;
using Base.OrderAccumulator.Application.Orders.Responses;
using Base.OrderAccumulator.Commons.Database;

namespace Base.OrderAccumulator.Infrastructure.Orders.Repositories;

public sealed class OrderListReadRepository(IDatabase orderDatabase) : IOrderListReadRepository
{
    public const int OrdersPerPage = 10;

    private const string CountStoredOrdersSql = "SELECT count(*) FROM orders";

    private const string SelectStoredOrderPageSql = """
        SELECT received_at AS ReceivedAt, accepted AS Accepted, symbol AS Symbol, side AS Side,
               quantity AS Quantity, price AS Price, order_id AS OrderId, cl_ord_id AS ClOrdId
        FROM orders
        ORDER BY received_at DESC, id DESC
        LIMIT @OrdersPerPage OFFSET @SkippedOrders
        """;

    public Task<StoredOrderPageResponse> ReadStoredOrderPageAsync(int pageNumber, CancellationToken cancellationToken = default) =>
        orderDatabase.ReadInRepeatableReadSnapshotAsync(async () =>
        {
            var totalStoredOrders = await orderDatabase.QueryScalarAsync<long>(CountStoredOrdersSql, null, cancellationToken);
            var storedOrdersOfPage = await orderDatabase.QueryRecordsAsync<StoredOrderResponse>(
                SelectStoredOrderPageSql, new { OrdersPerPage, SkippedOrders = (pageNumber - 1) * OrdersPerPage }, cancellationToken);
            return new StoredOrderPageResponse(totalStoredOrders, storedOrdersOfPage);
        }, cancellationToken);
}
