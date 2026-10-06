using Flowa.DatadogMetrics.Application.Orders.UseCases;

namespace Flowa.DatadogMetrics.Tests;

[Collection(DatadogMetricsPostgresCollection.Name)]
public sealed class AnsweredOrderCountTests(DatadogMetricsPostgresFixture datadogMetricsDatabase) : IDisposable
{
    private const string BuyOrderSide = FlowaTestDatabase.BuyOrderSide;
    private const string SellOrderSide = FlowaTestDatabase.SellOrderSide;

    private readonly DogStatsdUdpListener dogStatsdUdpListener = new();

    [Fact]
    public async Task Two_cycles_count_buys_and_sells_accepted_and_rejected_once_each_with_symbol_and_side()
    {
        // Arrange
        var flowaTestDatabase = await datadogMetricsDatabase.CreateFlowaDatabaseWithTheThreeExposuresAsync();
        await using var datadogMetricsWorker = new DatadogMetricsTestWorker(flowaTestDatabase.FlowaConnectionString, dogStatsdUdpListener, TimeProvider.System);
        await datadogMetricsWorker.RunOneMetricsCycleAndReadSentMetricsAsync();
        await flowaTestDatabase.StoreAnsweredOrderAsync("PETR4", BuyOrderSide, accepted: true);
        await flowaTestDatabase.StoreAnsweredOrderAsync("PETR4", BuyOrderSide, accepted: true);
        await flowaTestDatabase.StoreAnsweredOrderAsync("VALE3", SellOrderSide, accepted: true);
        await flowaTestDatabase.StoreAnsweredOrderAsync("PETR4", SellOrderSide, accepted: false);
        await flowaTestDatabase.StoreAnsweredOrderAsync("VIIA4", BuyOrderSide, accepted: false);

        // Act
        var firstCycleOrderCounts = AnsweredOrderCountsOf(await datadogMetricsWorker.RunOneMetricsCycleAndReadSentMetricsAsync());
        await flowaTestDatabase.StoreAnsweredOrderAsync("VALE3", BuyOrderSide, accepted: false);
        await flowaTestDatabase.StoreAnsweredOrderAsync("PETR4", BuyOrderSide, accepted: true);
        var secondCycleOrderCounts = AnsweredOrderCountsOf(await datadogMetricsWorker.RunOneMetricsCycleAndReadSentMetricsAsync());
        var cycleWithoutNewOrdersCounts = AnsweredOrderCountsOf(await datadogMetricsWorker.RunOneMetricsCycleAndReadSentMetricsAsync());

        // Assert
        Assert.Equal(
            DatadogMetricsTestWorker.InComparisonOrder([
                AcceptedOrderCount("2", "symbol:PETR4", "side:buy"),
                AcceptedOrderCount("1", "symbol:VALE3", "side:sell"),
                RejectedOrderCount("1", "symbol:PETR4", "side:sell"),
                RejectedOrderCount("1", "symbol:VIIA4", "side:buy")
            ]),
            firstCycleOrderCounts);
        Assert.Equal(
            DatadogMetricsTestWorker.InComparisonOrder([
                AcceptedOrderCount("1", "symbol:PETR4", "side:buy"),
                RejectedOrderCount("1", "symbol:VALE3", "side:buy")
            ]),
            secondCycleOrderCounts);
        Assert.Empty(cycleWithoutNewOrdersCounts);
    }

    [Fact]
    public async Task Orders_stored_before_the_worker_starts_are_not_counted()
    {
        // Arrange
        var flowaTestDatabase = await datadogMetricsDatabase.CreateFlowaDatabaseWithTheThreeExposuresAsync();
        await flowaTestDatabase.StoreAnsweredOrderAsync("PETR4", BuyOrderSide, accepted: true);
        await flowaTestDatabase.StoreAnsweredOrderAsync("VALE3", SellOrderSide, accepted: false);
        await using var datadogMetricsWorker = new DatadogMetricsTestWorker(flowaTestDatabase.FlowaConnectionString, dogStatsdUdpListener, TimeProvider.System);

        // Act
        var startupCycleOrderCounts = AnsweredOrderCountsOf(await datadogMetricsWorker.RunOneMetricsCycleAndReadSentMetricsAsync());
        var nextCycleOrderCounts = AnsweredOrderCountsOf(await datadogMetricsWorker.RunOneMetricsCycleAndReadSentMetricsAsync());

        // Assert
        Assert.Empty(startupCycleOrderCounts);
        Assert.Empty(nextCycleOrderCounts);
        Assert.Equal(
            ["Information Order count starts after the stored orders.", "Information Answered order counts sent."],
            datadogMetricsWorker.LoggerOf<SendAnsweredOrderCountsUseCase>().RecordedLogLines);
    }

    [Fact]
    public async Task Restarting_the_worker_does_not_count_again_the_orders_counted_before_the_restart()
    {
        // Arrange
        var flowaTestDatabase = await datadogMetricsDatabase.CreateFlowaDatabaseWithTheThreeExposuresAsync();
        await using (var workerBeforeTheRestart = new DatadogMetricsTestWorker(flowaTestDatabase.FlowaConnectionString, dogStatsdUdpListener, TimeProvider.System))
        {
            await workerBeforeTheRestart.RunOneMetricsCycleAndReadSentMetricsAsync();
            await flowaTestDatabase.StoreAnsweredOrderAsync("PETR4", BuyOrderSide, accepted: true);
            Assert.Equal([AcceptedOrderCount("1", "symbol:PETR4", "side:buy")],
                AnsweredOrderCountsOf(await workerBeforeTheRestart.RunOneMetricsCycleAndReadSentMetricsAsync()));
        }

        await using var workerAfterTheRestart = new DatadogMetricsTestWorker(flowaTestDatabase.FlowaConnectionString, dogStatsdUdpListener, TimeProvider.System);

        // Act
        var restartCycleOrderCounts = AnsweredOrderCountsOf(await workerAfterTheRestart.RunOneMetricsCycleAndReadSentMetricsAsync());
        await flowaTestDatabase.StoreAnsweredOrderAsync("VIIA4", SellOrderSide, accepted: true);
        var cycleAfterTheRestartCounts = AnsweredOrderCountsOf(await workerAfterTheRestart.RunOneMetricsCycleAndReadSentMetricsAsync());

        // Assert
        Assert.Empty(restartCycleOrderCounts);
        Assert.Equal([AcceptedOrderCount("1", "symbol:VIIA4", "side:sell")], cycleAfterTheRestartCounts);
    }

    [Fact]
    public async Task Unknown_symbols_and_unknown_side_are_counted_under_invalido_and_never_as_the_stored_text()
    {
        // Arrange
        var flowaTestDatabase = await datadogMetricsDatabase.CreateFlowaDatabaseWithTheThreeExposuresAsync();
        await using var datadogMetricsWorker = new DatadogMetricsTestWorker(flowaTestDatabase.FlowaConnectionString, dogStatsdUdpListener, TimeProvider.System);
        await datadogMetricsWorker.RunOneMetricsCycleAndReadSentMetricsAsync();
        await flowaTestDatabase.StoreAnsweredOrderAsync("ABCD3-texto-livre", BuyOrderSide, accepted: false);
        await flowaTestDatabase.StoreAnsweredOrderAsync(null, SellOrderSide, accepted: false);
        await flowaTestDatabase.StoreAnsweredOrderAsync("PETR4", "3", accepted: false);

        // Act
        var invalidOrderCounts = AnsweredOrderCountsOf(await datadogMetricsWorker.RunOneMetricsCycleAndReadSentMetricsAsync());

        // Assert
        Assert.Equal(
            DatadogMetricsTestWorker.InComparisonOrder([
                RejectedOrderCount("1", "symbol:PETR4", "side:invalido"),
                RejectedOrderCount("2", "symbol:invalido", "side:invalido")
            ]),
            invalidOrderCounts);
    }

    [Fact]
    public async Task Orders_deleted_after_being_counted_are_not_counted_again_and_new_ones_still_count()
    {
        // Arrange
        var flowaTestDatabase = await datadogMetricsDatabase.CreateFlowaDatabaseWithTheThreeExposuresAsync();
        await using var datadogMetricsWorker = new DatadogMetricsTestWorker(flowaTestDatabase.FlowaConnectionString, dogStatsdUdpListener, TimeProvider.System);
        await datadogMetricsWorker.RunOneMetricsCycleAndReadSentMetricsAsync();
        await flowaTestDatabase.StoreAnsweredOrderAsync("VALE3", SellOrderSide, accepted: true);
        await datadogMetricsWorker.RunOneMetricsCycleAndReadSentMetricsAsync();
        await flowaTestDatabase.DeleteAllStoredOrdersAsync();
        await flowaTestDatabase.StoreAnsweredOrderAsync("VALE3", BuyOrderSide, accepted: true);

        // Act
        var cycleAfterTheDeleteCounts = AnsweredOrderCountsOf(await datadogMetricsWorker.RunOneMetricsCycleAndReadSentMetricsAsync());

        // Assert
        Assert.Equal([AcceptedOrderCount("1", "symbol:VALE3", "side:buy")], cycleAfterTheDeleteCounts);
    }

    private static List<DogStatsdMetricLine> AnsweredOrderCountsOf(IEnumerable<DogStatsdMetricLine> sentMetrics) =>
        DatadogMetricsTestWorker.InComparisonOrder(sentMetrics.Where(sentMetric => sentMetric.MetricName.StartsWith("flowa.ordens.")));

    private static DogStatsdMetricLine AcceptedOrderCount(string orderCount, params string[] orderTags) =>
        new("flowa.ordens.aceitas", orderCount, "c", DatadogMetricsTestWorker.ExpectedTags(orderTags));

    private static DogStatsdMetricLine RejectedOrderCount(string orderCount, params string[] orderTags) =>
        new("flowa.ordens.rejeitadas", orderCount, "c", DatadogMetricsTestWorker.ExpectedTags(orderTags));

    public void Dispose() => dogStatsdUdpListener.Dispose();
}
