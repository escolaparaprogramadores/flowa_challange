using Dapper;

namespace Flowa.OrderAccumulator.Tests;

// CA-13, CA-14 and CA-15 on the database side of the order list: the worker has no HTTP, so it is checked that each
// decided order is stored with the nine columns the Generator lists (status, symbol, side, quantity, price, ids, reason
// and arrival), and that the schema creates the index the list reads by. Paging, sorting, the JSON names and the page
// validation of GET /api/orders live in the Generator (StoredOrdersDatabaseTests).
[Collection(OrderAccumulatorPostgresCollection.Name)]
public sealed class StoredOrderColumnsTests(OrderAccumulatorPostgresFixture orderAccumulatorDatabase) : IAsyncLifetime
{
    private const string SelectStoredOrderRowsInArrivalOrderSql = """
        SELECT received_at AS ReceivedAt, accepted AS Accepted, symbol AS Symbol, side AS Side, quantity AS Quantity,
               price AS Price, order_id AS OrderId, cl_ord_id AS ClOrdId, reject_reason AS RejectReason
        FROM orders
        ORDER BY id
        """;

    public Task InitializeAsync() => orderAccumulatorDatabase.ResetOrdersAndExposuresAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Accepted_and_rejected_orders_are_stored_with_the_nine_columns_the_order_list_reads()
    {
        // Arrange
        await using var orderAccumulatorTestApp = await new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptorAsync();
        var appOrderDecisionServices = orderAccumulatorTestApp.Services;

        var databaseClockBeforeTheOrders = await ReadDatabaseClockAsync();

        // Act
        var acceptedBuyOutcome = await appOrderDecisionServices.DecideIncomingOrderAsync(TestOrders.NewBuyOrder("PETR4", 100, 10.50m));
        var rejectedSellOutcome = await appOrderDecisionServices.DecideIncomingOrderAsync(TestOrders.NewSellOrder("VALE3", 100_000, 1.00m));
        var databaseClockAfterTheOrders = await ReadDatabaseClockAsync();
        var storedOrderRows = await ReadStoredOrderRowsInArrivalOrderAsync();

        // Assert
        Assert.False(rejectedSellOutcome.Accepted);
        Assert.Equal(2, storedOrderRows.Count);
        Assert.Equal(
            (true, "PETR4", "1", 100m, 10.50m, acceptedBuyOutcome.OrderId, acceptedBuyOutcome.ClOrdId, (string?)null),
            ReadListedColumnsOf(storedOrderRows[0]));
        Assert.Equal(
            (false, "VALE3", "2", 100_000m, 1.00m, rejectedSellOutcome.OrderId, rejectedSellOutcome.ClOrdId, "A quantidade deve ser menor que 100.000."),
            ReadListedColumnsOf(storedOrderRows[1]));
        Assert.InRange(storedOrderRows[0].ReceivedAt, databaseClockBeforeTheOrders, storedOrderRows[1].ReceivedAt);
        Assert.InRange(storedOrderRows[1].ReceivedAt, storedOrderRows[0].ReceivedAt, databaseClockAfterTheOrders);
    }

    [Fact]
    public async Task Order_rejected_over_the_exposure_limit_is_stored_with_the_limit_reason_and_the_accepted_one_with_null()
    {
        // Arrange
        await using var orderAccumulatorTestApp = await new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptorAsync();
        var appOrderDecisionServices = orderAccumulatorTestApp.Services;
        // 2 × 50,000 × 999.99 = 99,999,000; another 2,000 × 1.00 would exceed 100,000,000.
        await appOrderDecisionServices.DecideIncomingOrderAsync(TestOrders.NewBuyOrder("VALE3", 50_000, 999.99m));

        // Act
        var acceptedBuyOutcome = await appOrderDecisionServices.DecideIncomingOrderAsync(TestOrders.NewBuyOrder("VALE3", 50_000, 999.99m));
        var overLimitOutcome = await appOrderDecisionServices.DecideIncomingOrderAsync(TestOrders.NewBuyOrder("VALE3", 2_000, 1.00m));
        var storedOrderRows = await ReadStoredOrderRowsInArrivalOrderAsync();

        // Assert
        Assert.Equal(
            [(acceptedBuyOutcome.ClOrdId, true, null), (overLimitOutcome.ClOrdId, false, "Ordem rejeitada: a exposição de VALE3 passaria do limite de 100.000.000,00.")],
            storedOrderRows.Skip(1).Select(storedOrderRow => (storedOrderRow.ClOrdId, storedOrderRow.Accepted, storedOrderRow.RejectReason)).ToList());
    }

    [Fact]
    public async Task Rejected_order_with_unknown_symbol_and_side_is_stored_with_the_symbol_and_the_side_code_it_came_with()
    {
        // Arrange
        await using var orderAccumulatorTestApp = await new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptorAsync();

        // Act
        var rejectedUnknownSideOutcome = await orderAccumulatorTestApp.Services.DecideIncomingOrderAsync(TestOrders.NewIncomingOrder("ABCD3", '7', 10, 1.00m));
        var storedOrderRow = Assert.Single(await ReadStoredOrderRowsInArrivalOrderAsync());

        // Assert
        Assert.False(rejectedUnknownSideOutcome.Accepted);
        Assert.Equal((false, "ABCD3", "7"), (storedOrderRow.Accepted, storedOrderRow.Symbol, storedOrderRow.Side));
    }

    [Fact]
    public async Task Schema_creates_the_received_at_and_id_descending_index_on_orders()
    {
        // Arrange
        await using var orderDatabaseConnection = await orderAccumulatorDatabase.OrderDatabaseDataSource.OpenConnectionAsync();

        // Act
        var orderListIndexDefinition = await orderDatabaseConnection.ExecuteScalarAsync<string>(
            "SELECT indexdef FROM pg_indexes WHERE tablename = 'orders' AND indexname = 'orders_received_at_id_idx'");

        // Assert
        Assert.Equal("CREATE INDEX orders_received_at_id_idx ON public.orders USING btree (received_at DESC, id DESC)", orderListIndexDefinition);
    }

    private async Task<List<StoredOrderRow>> ReadStoredOrderRowsInArrivalOrderAsync()
    {
        await using var orderDatabaseConnection = await orderAccumulatorDatabase.OrderDatabaseDataSource.OpenConnectionAsync();
        return (await orderDatabaseConnection.QueryAsync<StoredOrderRow>(SelectStoredOrderRowsInArrivalOrderSql)).ToList();
    }

    private async Task<DateTime> ReadDatabaseClockAsync()
    {
        await using var orderDatabaseConnection = await orderAccumulatorDatabase.OrderDatabaseDataSource.OpenConnectionAsync();
        return await orderDatabaseConnection.ExecuteScalarAsync<DateTime>("SELECT clock_timestamp()");
    }

    private static (bool, string?, string, decimal, decimal, string, string, string?) ReadListedColumnsOf(StoredOrderRow storedOrderRow) =>
        (storedOrderRow.Accepted, storedOrderRow.Symbol, storedOrderRow.Side, storedOrderRow.Quantity, storedOrderRow.Price,
            storedOrderRow.OrderId, storedOrderRow.ClOrdId, storedOrderRow.RejectReason);

    private sealed record StoredOrderRow(
        DateTime ReceivedAt, bool Accepted, string? Symbol, string Side, decimal Quantity, decimal Price, string OrderId, string ClOrdId, string? RejectReason);
}
