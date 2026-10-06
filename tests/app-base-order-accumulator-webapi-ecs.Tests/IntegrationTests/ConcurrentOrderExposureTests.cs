using Base.OrderAccumulator.Domain.Exposures.ValueObjects;
using Base.OrderAccumulator.Domain.Orders.Enums;
using Dapper;
using Xunit.Abstractions;

namespace Base.OrderAccumulator.Tests;

// CA-12: the parallelism is in the data layer. Each order opens its own connection and transaction.
[Collection(OrderAccumulatorPostgresCollection.Name)]
public sealed class ConcurrentOrderExposureTests(OrderAccumulatorPostgresFixture orderAccumulatorDatabase, ITestOutputHelper concurrencyTestOutput)
{
    private const int SimultaneousOrders = 200;

    private const string CountTransactionsWaitingOnExposureRowSql = """
        SELECT count(*)
        FROM pg_stat_activity
        WHERE datname = current_database()
          AND wait_event_type = 'Lock'
          AND query LIKE '%UPDATE exposures%'
        """;

    // Quantities from 5,000 to 99,999 at 20.00: about 1 million per order, close to 2× the limit
    // in total, so part of them must be rejected. Odd rounds buy, even rounds sell.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task Simultaneous_orders_on_one_symbol_never_push_the_exposure_past_the_limit(int concurrencyRound)
    {
        await orderAccumulatorDatabase.ResetOrdersAndExposuresAsync();
        var orderSide = concurrencyRound % 2 == 1 ? OrderSideCodes.BuyOrderSideFixCode : OrderSideCodes.SellOrderSideFixCode;
        var orderQuantityGenerator = new Random(concurrencyRound);
        var simultaneousOrders = Enumerable.Range(0, SimultaneousOrders)
            .Select(_ => TestOrders.NewIncomingOrder("PETR4", orderSide, orderQuantityGenerator.Next(5_000, 100_000), 20.00m))
            .ToList();

        // A separate transaction holds the PETR4 row. That way the 200 order transactions all reach
        // the conditional UPDATE and wait together, inside PostgreSQL.
        await using var exposureRowHolderConnection = await orderAccumulatorDatabase.OrderDatabaseDataSource.OpenConnectionAsync();
        await using var exposureRowHolderTransaction = await exposureRowHolderConnection.BeginTransactionAsync();
        await exposureRowHolderConnection.ExecuteAsync(
            "SELECT exposure FROM exposures WHERE symbol = 'PETR4' FOR UPDATE", transaction: exposureRowHolderTransaction);

        var simultaneousOrderTasks = simultaneousOrders
            .Select(incomingOrder => Task.Run(() => orderAccumulatorDatabase.OrderDecisionRunner.DecideIncomingOrderAsync(incomingOrder)))
            .ToList();
        var transactionsWaitingTogether = await WaitForTransactionsWaitingOnExposureRowAsync(SimultaneousOrders);

        await exposureRowHolderTransaction.CommitAsync();
        var orderAnswers = await Task.WhenAll(simultaneousOrderTasks);

        var finalExposure = await orderAccumulatorDatabase.ReadExposureOfSymbolAsync("PETR4");
        var acceptedAnswers = orderAnswers.Where(orderAnswer => orderAnswer.Accepted).ToList();
        var rejectedAnswers = orderAnswers.Where(orderAnswer => !orderAnswer.Accepted).ToList();
        concurrencyTestOutput.WriteLine(
            $"round {concurrencyRound}: {transactionsWaitingTogether} transactions waiting for the row at the same time, " +
            $"{acceptedAnswers.Count} accepted, {rejectedAnswers.Count} rejected, final exposure {finalExposure}");

        Assert.Equal(SimultaneousOrders, transactionsWaitingTogether);
        Assert.True(Math.Abs(finalExposure) <= ExposureLimitPolicy.PerSymbol, $"exposure {finalExposure} went over the limit");
        Assert.Equal(acceptedAnswers.Sum(TestOrders.ExposureDeltaOf), finalExposure);
        Assert.Equal(finalExposure, await orderAccumulatorDatabase.SumAcceptedOrdersExposureAsync("PETR4"));
        Assert.Equal(SimultaneousOrders, await orderAccumulatorDatabase.CountStoredOrdersAsync());
        Assert.NotEmpty(rejectedAnswers);
        Assert.All(rejectedAnswers, rejectedAnswer =>
            Assert.Equal(ExposureLimitPolicy.BuildExposureLimitRejectionText("PETR4"), rejectedAnswer.RejectReason));

        // The exposure only moves in one direction in this round; so every rejected order was larger than the
        // room left at the end. This shows the accepted ones reached the limit.
        Assert.All(rejectedAnswers, rejectedAnswer =>
            Assert.True(Math.Abs(TestOrders.ExposureDeltaOf(rejectedAnswer)) > ExposureLimitPolicy.CalculateRemainingExposureCapacity(finalExposure)));
    }

    // Regression of the flaky test: with 30 s (the Npgsql default) a slow round timed out the read.
    [Fact]
    public async Task Order_database_connections_of_the_tests_wait_120_seconds_per_command()
    {
        await using var orderDatabaseConnection = await orderAccumulatorDatabase.OrderDatabaseDataSource.OpenConnectionAsync();

        Assert.Equal(120, orderDatabaseConnection.CommandTimeout);
    }

    // Waits until all transactions are stopped on the row lock, or 30 s.
    private async Task<long> WaitForTransactionsWaitingOnExposureRowAsync(int expectedWaitingTransactions)
    {
        await using var lockWaitMonitorConnection = await orderAccumulatorDatabase.OrderDatabaseDataSource.OpenConnectionAsync();
        var lockWaitDeadline = DateTime.UtcNow.AddSeconds(30);
        long transactionsWaitingOnExposureRow;
        do
        {
            transactionsWaitingOnExposureRow = await lockWaitMonitorConnection.ExecuteScalarAsync<long>(CountTransactionsWaitingOnExposureRowSql);
            if (transactionsWaitingOnExposureRow >= expectedWaitingTransactions)
                return transactionsWaitingOnExposureRow;
            await Task.Delay(50);
        } while (DateTime.UtcNow < lockWaitDeadline);

        return transactionsWaitingOnExposureRow;
    }
}
