using Dapper;
using Flowa.Shared;
using OrderAccumulator.Exposure;
using Xunit.Abstractions;

namespace OrderAccumulator.Tests;

// CA-12: o paralelismo é na camada de dados. Cada ordem abre a própria conexão e transação.
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

    // Quantidades de 5.000 a 99.999 a 20,00: cerca de 1 milhão por ordem, perto de 2× o limite
    // no total, então parte tem de ser rejeitada. Rodadas ímpares compram, pares vendem.
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

        // Uma transação à parte segura a linha de PETR4. Assim as 200 transações das ordens chegam
        // todas ao UPDATE condicional e ficam esperando juntas, dentro do PostgreSQL.
        await using var exposureRowHolderConnection = await orderAccumulatorDatabase.OrderDatabaseDataSource.OpenConnectionAsync();
        await using var exposureRowHolderTransaction = await exposureRowHolderConnection.BeginTransactionAsync();
        await exposureRowHolderConnection.ExecuteAsync(
            "SELECT exposure FROM exposures WHERE symbol = 'PETR4' FOR UPDATE", transaction: exposureRowHolderTransaction);

        var simultaneousOrderTasks = simultaneousOrders
            .Select(incomingOrder => Task.Run(() => orderAccumulatorDatabase.OrderProcessor.ProcessIncomingOrderAsync(incomingOrder)))
            .ToList();
        var transactionsWaitingTogether = await WaitForTransactionsWaitingOnExposureRowAsync(SimultaneousOrders);

        await exposureRowHolderTransaction.CommitAsync();
        var orderAnswers = await Task.WhenAll(simultaneousOrderTasks);

        var finalExposure = await orderAccumulatorDatabase.ReadExposureOfSymbolAsync("PETR4");
        var acceptedAnswers = orderAnswers.Where(orderAnswer => orderAnswer.Accepted).ToList();
        var rejectedAnswers = orderAnswers.Where(orderAnswer => !orderAnswer.Accepted).ToList();
        concurrencyTestOutput.WriteLine(
            $"rodada {concurrencyRound}: {transactionsWaitingTogether} transações esperando a linha ao mesmo tempo, " +
            $"{acceptedAnswers.Count} aceitas, {rejectedAnswers.Count} rejeitadas, exposição final {finalExposure}");

        Assert.Equal(SimultaneousOrders, transactionsWaitingTogether);
        Assert.True(Math.Abs(finalExposure) <= ExposureLimit.PerSymbol, $"exposição {finalExposure} passou do limite");
        Assert.Equal(acceptedAnswers.Sum(TestOrders.ExposureDeltaOf), finalExposure);
        Assert.Equal(finalExposure, await orderAccumulatorDatabase.SumAcceptedOrdersExposureAsync("PETR4"));
        Assert.Equal(SimultaneousOrders, await orderAccumulatorDatabase.CountStoredOrdersAsync());
        Assert.NotEmpty(rejectedAnswers);
        Assert.All(rejectedAnswers, rejectedAnswer =>
            Assert.Equal(ExposureLimit.ExposureLimitRejectionText("PETR4"), rejectedAnswer.RejectReason));

        // A exposição só anda num sentido nesta rodada; então toda rejeitada era maior do que a
        // folga que sobrou no fim. Isso mostra que as aceitas encostaram no limite.
        Assert.All(rejectedAnswers, rejectedAnswer =>
            Assert.True(Math.Abs(TestOrders.ExposureDeltaOf(rejectedAnswer)) > ExposureLimit.RemainingExposureCapacity(finalExposure)));
    }

    // Regressão do teste instável: com 30 s (padrão do Npgsql) uma rodada lenta estourava a leitura.
    [Fact]
    public async Task Order_database_connections_of_the_tests_wait_120_seconds_per_command()
    {
        await using var orderDatabaseConnection = await orderAccumulatorDatabase.OrderDatabaseDataSource.OpenConnectionAsync();

        Assert.Equal(120, orderDatabaseConnection.CommandTimeout);
    }

    // Espera até todas as transações estarem paradas no lock da linha, ou 30 s.
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
