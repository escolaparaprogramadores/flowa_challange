using System.Data;
using Flowa.OrderAccumulator.Domain.Exposures.ValueObjects;
using Flowa.OrderAccumulator.Domain.Orders.Entities;
using Flowa.OrderAccumulator.Domain.Orders.Enums;
using Flowa.OrderAccumulator.Domain.Orders.Interfaces;
using Flowa.OrderAccumulator.Domain.Orders.ValueObjects;
using Dapper;
using Xunit.Abstractions;

namespace Flowa.OrderAccumulator.Tests;

// CA-17, CA-42 and O-12: the exposure lives only in the database. An order and "Delete all" are put in line by the
// exposure rows themselves, in READ COMMITTED, and in the end the exposure equals the sum of the accepted orders that remain.
// "Delete all" is the Generator's DELETE /api/orders, played here straight in the database as it runs there: one READ
// COMMITTED transaction that zeroes the three exposure rows and then deletes the orders.
[Collection(OrderAccumulatorPostgresCollection.Name)]
public sealed class DeleteAllOrdersConcurrencyTests(OrderAccumulatorPostgresFixture orderAccumulatorDatabase, ITestOutputHelper concurrencyTestOutput)
{
    private const int OrdersPerWave = 100;
    private const int StoredOrdersCountBeforeFiringTheDeletes = 30;
    private const int SimultaneousDeletesPerRound = 5;

    private const string CountTransactionsWaitingOnExposureRowsSql = """
        SELECT count(*)
        FROM pg_stat_activity
        WHERE datname = current_database()
          AND wait_event_type = 'Lock'
          AND query LIKE '%UPDATE exposures%'
        """;

    private const string ZeroSymbolExposuresAsTheGeneratorDoesSql = "UPDATE exposures SET exposure = 0 WHERE symbol = ANY(@Symbols)";
    private const string DeleteAllOrdersAsTheGeneratorDoesSql = "DELETE FROM orders";

    private static readonly TimeSpan ConcurrencyStepDeadline = TimeSpan.FromSeconds(30);

    private static readonly SymbolExposure[] ZeroedSymbolExposures =
        [new("PETR4", 0m), new("VALE3", 0m), new("VIIA4", 0m)];

    [Fact]
    public async Task Delete_waits_for_the_order_holding_the_exposure_row_and_zeroes_after_it_commits()
    {
        await orderAccumulatorDatabase.ResetOrdersAndExposuresAsync();
        OrderStorageHeldUntilReleased heldOrderStorage = null!;
        var orderHoldingTheExposureRow = Task.Run(() => new DecideIncomingOrderTestRunner(
                orderAccumulatorDatabase.OrderDatabaseConnectionSource, new UncountedOrderMetrics(),
                storedOrderRepository => heldOrderStorage = new OrderStorageHeldUntilReleased(storedOrderRepository))
            .DecideIncomingOrderAsync(TestOrders.NewBuyOrder("PETR4", 100, 10.00m)));
        await WaitUntilAsync(() => heldOrderStorage is not null);
        await heldOrderStorage.StorageReached.WaitAsync(ConcurrencyStepDeadline);

        var deleteAllOrdersTask = Task.Run(() => DeleteAllOrdersAsTheGeneratorDoesAsync());
        var deletesWaitingOnTheExposureRows = await WaitForTransactionsWaitingOnExposureRowsAsync(1);
        var hadDeleteFinishedWhileTheOrderHeldTheRow = deleteAllOrdersTask.IsCompleted;
        heldOrderStorage.ReleaseTheStorage();
        var heldOrderDecision = await orderHoldingTheExposureRow.WaitAsync(ConcurrencyStepDeadline);
        await deleteAllOrdersTask.WaitAsync(ConcurrencyStepDeadline);

        Assert.Equal(1, deletesWaitingOnTheExposureRows);
        Assert.False(hadDeleteFinishedWhileTheOrderHeldTheRow);
        Assert.True(heldOrderDecision.Accepted);
        Assert.Equal(ZeroedSymbolExposures, await orderAccumulatorDatabase.ExposureReader.GetSymbolExposuresAsync());
        Assert.Equal(0L, await orderAccumulatorDatabase.CountStoredOrdersAsync());
    }

    // The order arrives while the delete holds the zeroed rows and fits the limit both before and after the zero. In
    // READ COMMITTED it waits for the delete and moves the zeroed exposure: -1,000,000 in the end, not 98,998,000.01.
    // In REPEATABLE READ this same order would fail with a serialization error.
    [Fact]
    public async Task Order_arriving_during_a_delete_waits_and_moves_the_zeroed_exposure()
    {
        await orderAccumulatorDatabase.ResetOrdersAndExposuresAsync();
        var orderNearTheLimit = await orderAccumulatorDatabase.OrderDecisionRunner.DecideIncomingOrderAsync(
            TestOrders.NewBuyOrder("PETR4", OrderFieldPolicy.MaxOrderQuantityExclusive - 1, 999.99m));
        var heldOrderDeletion = new OrderDeletionHeldUntilReleased();
        var deleteHoldingTheZeroedRows = Task.Run(() => DeleteAllOrdersAsTheGeneratorDoesAsync(heldOrderDeletion.WaitForTheReleaseAsync));
        await heldOrderDeletion.DeletionReached.WaitAsync(ConcurrencyStepDeadline);

        var sellOrderTask = Task.Run(() => orderAccumulatorDatabase.OrderDecisionRunner.DecideIncomingOrderAsync(
            TestOrders.NewSellOrder("PETR4", 10_000, 100.00m)));
        var ordersWaitingOnTheZeroedRows = await WaitForTransactionsWaitingOnExposureRowsAsync(1);
        var hadOrderFinishedWhileTheDeleteHeldTheRows = sellOrderTask.IsCompleted;
        heldOrderDeletion.ReleaseTheDeletion();
        await deleteHoldingTheZeroedRows.WaitAsync(ConcurrencyStepDeadline);
        var sellOrderDecision = await sellOrderTask.WaitAsync(ConcurrencyStepDeadline);

        Assert.Equal(99_998_000.01m, TestOrders.ExposureDeltaOf(orderNearTheLimit));
        Assert.Equal(1, ordersWaitingOnTheZeroedRows);
        Assert.False(hadOrderFinishedWhileTheDeleteHeldTheRows);
        Assert.True(sellOrderDecision.Accepted, sellOrderDecision.RejectReason);
        Assert.Equal(-1_000_000.00m, await orderAccumulatorDatabase.ReadExposureOfSymbolAsync("PETR4"));
        Assert.Equal(1L, await orderAccumulatorDatabase.CountStoredOrdersAsync());
        Assert.Equal(-1_000_000.00m, await orderAccumulatorDatabase.SumAcceptedOrdersExposureAsync("PETR4"));
    }

    // The same rule, the other way: an order that only fits after the zero, arriving while the delete is still open, is
    // decided against the last committed exposure without waiting (the conditional UPDATE skips a row that does not
    // match). It is rejected as if it came before the delete, and the delete then removes it with the others: in the
    // end nothing is left and the exposure is zero, equal to the sum of the accepted orders that remain.
    [Fact]
    public async Task Order_that_only_fits_after_the_zero_and_arrives_during_a_delete_is_decided_against_the_committed_exposure()
    {
        await orderAccumulatorDatabase.ResetOrdersAndExposuresAsync();
        await orderAccumulatorDatabase.OrderDecisionRunner.DecideIncomingOrderAsync(
            TestOrders.NewBuyOrder("PETR4", OrderFieldPolicy.MaxOrderQuantityExclusive - 1, 999.99m));
        var heldOrderDeletion = new OrderDeletionHeldUntilReleased();
        var deleteHoldingTheZeroedRows = Task.Run(() => DeleteAllOrdersAsTheGeneratorDoesAsync(heldOrderDeletion.WaitForTheReleaseAsync));
        await heldOrderDeletion.DeletionReached.WaitAsync(ConcurrencyStepDeadline);

        var largeOrderDecision = await orderAccumulatorDatabase.OrderDecisionRunner.DecideIncomingOrderAsync(
            TestOrders.NewBuyOrder("PETR4", 10_000, 100.00m)).WaitAsync(ConcurrencyStepDeadline);
        heldOrderDeletion.ReleaseTheDeletion();
        await deleteHoldingTheZeroedRows.WaitAsync(ConcurrencyStepDeadline);

        Assert.False(largeOrderDecision.Accepted);
        Assert.Equal(ExposureLimitPolicy.BuildExposureLimitRejectionText("PETR4"), largeOrderDecision.RejectReason);
        Assert.Equal(ZeroedSymbolExposures, await orderAccumulatorDatabase.ExposureReader.GetSymbolExposuresAsync());
        Assert.Equal(0L, await orderAccumulatorDatabase.CountStoredOrdersAsync());
    }

    // Through the whole app: 100 orders on the three symbols; when 30 are already stored, 5 deletes of the Generator come
    // in among the ones still in progress; after the deletes, 100 more orders. No exception and no deadlock; in the end
    // orders remain (the ones that came after the last delete) and the exposure in the database equals the sum of the
    // remaining accepted orders.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task Deletes_in_the_middle_of_orders_in_progress_end_with_the_exposure_equal_to_the_remaining_accepted_orders(int concurrencyRound)
    {
        await orderAccumulatorDatabase.ResetOrdersAndExposuresAsync();
        await using var orderAccumulatorTestApp = await new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptorAsync();
        var appOrderDecisionServices = orderAccumulatorTestApp.Services;
        var orderQuantityGenerator = new Random(concurrencyRound);
        var storedOrdersCountBeforeTheDeletes = 0;
        var enoughOrdersStoredToFireTheDeletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var ordersBeforeTheDeletes = NewOrdersMixingSymbolsAndSides(OrdersPerWave, orderQuantityGenerator).Select(symbolAndSideMixedOrder => Task.Run(async () =>
        {
            var orderDecision = await appOrderDecisionServices.DecideIncomingOrderAsync(symbolAndSideMixedOrder);
            if (Interlocked.Increment(ref storedOrdersCountBeforeTheDeletes) == StoredOrdersCountBeforeFiringTheDeletes)
                enoughOrdersStoredToFireTheDeletes.TrySetResult();
            return orderDecision;
        })).ToList();
        await enoughOrdersStoredToFireTheDeletes.Task.WaitAsync(TimeSpan.FromMinutes(2));
        // Counts by the orders already stored, not by Task.IsCompleted: the task that fires the signal has not finished
        // yet when the test wakes up and would count as "in progress", adding one too many.
        var ordersInProgressCountWhenTheDeletesFired = OrdersPerWave - Volatile.Read(ref storedOrdersCountBeforeTheDeletes);
        await Task.WhenAll(Enumerable.Range(0, SimultaneousDeletesPerRound)
            .Select(_ => Task.Run(() => DeleteAllOrdersAsTheGeneratorDoesAsync()))).WaitAsync(TimeSpan.FromMinutes(2));
        var ordersAfterTheDeletes = NewOrdersMixingSymbolsAndSides(OrdersPerWave, orderQuantityGenerator)
            .Select(symbolAndSideMixedOrder => Task.Run(() => appOrderDecisionServices.DecideIncomingOrderAsync(symbolAndSideMixedOrder))).ToList();
        var orderDecisions = await Task.WhenAll(ordersBeforeTheDeletes.Concat(ordersAfterTheDeletes)).WaitAsync(TimeSpan.FromMinutes(2));

        var remainingStoredOrdersCount = await orderAccumulatorDatabase.CountStoredOrdersAsync();
        var storedExposures = await orderAccumulatorDatabase.ExposureReader.GetSymbolExposuresAsync();
        concurrencyTestOutput.WriteLine(
            $"round {concurrencyRound}: {ordersInProgressCountWhenTheDeletesFired} orders in progress when the deletes fired; " +
            $"{remainingStoredOrdersCount} orders remained; " +
            string.Join(", ", storedExposures.Select(storedExposure => $"{storedExposure.Symbol}={storedExposure.Exposure}")));
        Assert.All(orderDecisions, orderDecision => Assert.True(orderDecision.Accepted));
        Assert.InRange(ordersInProgressCountWhenTheDeletesFired, 1, OrdersPerWave - StoredOrdersCountBeforeFiringTheDeletes);
        Assert.InRange(remainingStoredOrdersCount, OrdersPerWave, 2 * OrdersPerWave - StoredOrdersCountBeforeFiringTheDeletes);
        foreach (var storedExposure in storedExposures)
            Assert.Equal(await orderAccumulatorDatabase.SumAcceptedOrdersExposureAsync(storedExposure.Symbol), storedExposure.Exposure);
    }

    private async Task DeleteAllOrdersAsTheGeneratorDoesAsync(Func<Task>? waitAfterZeroingTheExposures = null)
    {
        await using var generatorDeleteConnection = await orderAccumulatorDatabase.OrderDatabaseDataSource.OpenConnectionAsync();
        await using var generatorDeleteTransaction = await generatorDeleteConnection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await generatorDeleteConnection.ExecuteAsync(
            ZeroSymbolExposuresAsTheGeneratorDoesSql, new { Symbols = OrderFieldPolicy.AllowedOrderSymbols.ToArray() }, generatorDeleteTransaction);
        if (waitAfterZeroingTheExposures is not null)
            await waitAfterZeroingTheExposures();
        await generatorDeleteConnection.ExecuteAsync(DeleteAllOrdersAsTheGeneratorDoesSql, transaction: generatorDeleteTransaction);
        await generatorDeleteTransaction.CommitAsync();
    }

    private async Task<long> WaitForTransactionsWaitingOnExposureRowsAsync(int expectedWaitingTransactions)
    {
        await using var lockWaitMonitorConnection = await orderAccumulatorDatabase.OrderDatabaseDataSource.OpenConnectionAsync();
        var lockWaitDeadline = DateTime.UtcNow.Add(ConcurrencyStepDeadline);
        long transactionsWaitingOnExposureRows;
        do
        {
            transactionsWaitingOnExposureRows = await lockWaitMonitorConnection.ExecuteScalarAsync<long>(CountTransactionsWaitingOnExposureRowsSql);
            if (transactionsWaitingOnExposureRows >= expectedWaitingTransactions)
                return transactionsWaitingOnExposureRows;
            await Task.Delay(50);
        } while (DateTime.UtcNow < lockWaitDeadline);

        return transactionsWaitingOnExposureRows;
    }

    private static async Task WaitUntilAsync(Func<bool> conditionReached)
    {
        var conditionDeadline = DateTime.UtcNow.Add(ConcurrencyStepDeadline);
        while (!conditionReached() && DateTime.UtcNow < conditionDeadline)
            await Task.Delay(10);
    }

    private static List<IncomingOrder> NewOrdersMixingSymbolsAndSides(int orderCount, Random orderQuantityGenerator) =>
        Enumerable.Range(0, orderCount)
            .Select(orderNumber => TestOrders.NewIncomingOrder(
                OrderFieldPolicy.AllowedOrderSymbols[orderNumber % 3],
                orderNumber % 2 == 0 ? OrderSideCodes.BuyOrderSideFixCode : OrderSideCodes.SellOrderSideFixCode,
                orderQuantityGenerator.Next(1, 1_000), 10.00m))
            .ToList();

    // Plays the order repository of the order transaction: the exposure row was already moved, and the order waits
    // here, before being stored, until the test lets it go.
    private sealed class OrderStorageHeldUntilReleased(IOrderRepository storedOrderRepository) : IOrderRepository
    {
        private readonly TaskCompletionSource storageReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource storageReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StorageReached => storageReached.Task;

        public void ReleaseTheStorage() => storageReleased.SetResult();

        public Task<Order?> FindOrderByClOrdIdAsync(string clOrdId, CancellationToken cancellationToken = default) =>
            storedOrderRepository.FindOrderByClOrdIdAsync(clOrdId, cancellationToken);

        public async Task<bool> TryAddOrderAsync(Order answeredOrder, CancellationToken cancellationToken = default)
        {
            storageReached.SetResult();
            await storageReleased.Task;
            return await storedOrderRepository.TryAddOrderAsync(answeredOrder, cancellationToken);
        }
    }

    // Holds the delete of the Generator after it zeroed the three exposure rows and before it deletes the orders,
    // until the test lets it go.
    private sealed class OrderDeletionHeldUntilReleased
    {
        private readonly TaskCompletionSource deletionReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource deletionReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task DeletionReached => deletionReached.Task;

        public void ReleaseTheDeletion() => deletionReleased.SetResult();

        public Task WaitForTheReleaseAsync()
        {
            deletionReached.SetResult();
            return deletionReleased.Task;
        }
    }
}
