using Flowa.OrderAccumulator.Domain.Exposures.ValueObjects;
using Flowa.OrderAccumulator.Domain.Orders.ValueObjects;
using Dapper;

namespace Flowa.OrderAccumulator.Tests;

// CA-11, CA-13, CA-14 and CA-17: the worker has no HTTP, so the exposure each FIX order leaves is read straight in the
// database, where the Generator reads it for the screen. The app keeps no copy of it.
[Collection(OrderAccumulatorPostgresCollection.Name)]
public sealed class ExposureInTheDatabaseTests(OrderAccumulatorPostgresFixture orderAccumulatorDatabase) : IAsyncLifetime
{
    public Task InitializeAsync() => orderAccumulatorDatabase.ResetOrdersAndExposuresAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Accepted_orders_sent_by_fix_move_the_stored_exposures_and_a_rejected_one_does_not()
    {
        // Arrange
        await using var orderAccumulatorTestApp = await new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptorAsync();
        using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort);

        // Act
        await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder("buy-petr4", "PETR4", '1', 100, 10.50m));
        await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder("sell-vale3", "VALE3", '2', 20, 25.00m));
        var exposuresStoredAfterTheAcceptedOrders = await orderAccumulatorDatabase.ExposureReader.GetSymbolExposuresAsync();
        var rejectedOrderExecutionReport = await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder("rejected-viia4", "VIIA4", '1', 100_000, 1.00m));
        var exposuresStoredAfterTheRejectedOrder = await orderAccumulatorDatabase.ExposureReader.GetSymbolExposuresAsync();

        // Assert
        StoredSymbolExposure[] exposuresAfterTheAcceptedOrders = [new("PETR4", 1_050.00m), new("VALE3", -500.00m), new("VIIA4", 0m)];
        Assert.Equal(exposuresAfterTheAcceptedOrders, exposuresStoredAfterTheAcceptedOrders);
        Assert.Equal(QuickFix.Fields.ExecType.REJECTED, rejectedOrderExecutionReport.ExecType.Value);
        Assert.Equal("A quantidade deve ser menor que 100.000.", rejectedOrderExecutionReport.Text.Value);
        Assert.Equal(exposuresAfterTheAcceptedOrders, exposuresStoredAfterTheRejectedOrder);
    }

    // CA-17 (Accumulator part): another process zeroes the exposure and deletes the orders straight in the database, as
    // the Generator DELETE does; the next large order is decided against zero.
    [Fact]
    public async Task Exposure_zeroed_straight_in_the_database_lets_the_next_large_order_in()
    {
        // Arrange
        await using var orderAccumulatorTestApp = await new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptorAsync();
        var appOrderDecisionServices = orderAccumulatorTestApp.Services;
        var orderNearTheLimit = await appOrderDecisionServices.DecideIncomingOrderAsync(
            TestOrders.NewBuyOrder("PETR4", OrderFieldPolicy.MaxOrderQuantityExclusive - 1, 999.99m));
        var largeOrderBeforeTheZero = await appOrderDecisionServices.DecideIncomingOrderAsync(TestOrders.NewBuyOrder("PETR4", 10_000, 100.00m));

        // Act
        await using (var otherProcessConnection = await orderAccumulatorDatabase.OrderDatabaseDataSource.OpenConnectionAsync())
            await otherProcessConnection.ExecuteAsync("UPDATE exposures SET exposure = 0; DELETE FROM orders");
        var largeOrderAfterTheZero = await appOrderDecisionServices.DecideIncomingOrderAsync(TestOrders.NewBuyOrder("PETR4", 10_000, 100.00m));

        // Assert
        Assert.True(orderNearTheLimit.Accepted);
        Assert.False(largeOrderBeforeTheZero.Accepted);
        Assert.Equal(ExposureLimitPolicy.BuildExposureLimitRejectionText("PETR4"), largeOrderBeforeTheZero.RejectReason);
        Assert.True(largeOrderAfterTheZero.Accepted, largeOrderAfterTheZero.RejectReason);
        StoredSymbolExposure[] exposuresAfterTheLargeOrder = [new("PETR4", 1_000_000.00m), new("VALE3", 0m), new("VIIA4", 0m)];
        Assert.Equal(exposuresAfterTheLargeOrder, await orderAccumulatorDatabase.ExposureReader.GetSymbolExposuresAsync());
        Assert.Equal(1L, await orderAccumulatorDatabase.CountStoredOrdersAsync());
    }
}
