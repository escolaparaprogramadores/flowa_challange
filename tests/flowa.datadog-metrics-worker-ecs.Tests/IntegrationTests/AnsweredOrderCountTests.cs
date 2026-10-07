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
        var firstCycleOrderCounts = FilterAnsweredOrderCountLines(await datadogMetricsWorker.RunOneMetricsCycleAndReadSentMetricsAsync());
        await flowaTestDatabase.StoreAnsweredOrderAsync("VALE3", BuyOrderSide, accepted: false);
        await flowaTestDatabase.StoreAnsweredOrderAsync("PETR4", BuyOrderSide, accepted: true);
        var secondCycleOrderCounts = FilterAnsweredOrderCountLines(await datadogMetricsWorker.RunOneMetricsCycleAndReadSentMetricsAsync());
        var cycleWithoutNewOrdersCounts = FilterAnsweredOrderCountLines(await datadogMetricsWorker.RunOneMetricsCycleAndReadSentMetricsAsync());

        // Assert
        Assert.Equal(
            DatadogMetricsTestWorker.SortMetricLinesForComparison([
                BuildAcceptedOrderCountLine("2", "symbol:PETR4", "side:buy"),
                BuildAcceptedOrderCountLine("1", "symbol:VALE3", "side:sell"),
                BuildRejectedOrderCountLine("1", "symbol:PETR4", "side:sell"),
                BuildRejectedOrderCountLine("1", "symbol:VIIA4", "side:buy")
            ]),
            firstCycleOrderCounts);
        Assert.Equal(
            DatadogMetricsTestWorker.SortMetricLinesForComparison([
                BuildAcceptedOrderCountLine("1", "symbol:PETR4", "side:buy"),
                BuildRejectedOrderCountLine("1", "symbol:VALE3", "side:buy")
            ]),
            secondCycleOrderCounts);
        Assert.Empty(cycleWithoutNewOrdersCounts);
        var orderCountsSentContexts = datadogMetricsWorker.GetRecordingLoggerOf<SendAnsweredOrderCountsUseCase>().RecordedInformationContexts.Skip(1).ToList();
        Assert.Equal(3, orderCountsSentContexts.Count);
        Assert.Equal(
            ["PETR4/1/accepted=2", "PETR4/2/rejected=1", "VALE3/2/accepted=1", "VIIA4/1/rejected=1"],
            ReadSentOrderCountsOf(orderCountsSentContexts[0]));
        Assert.Equal(5, orderCountsSentContexts[0].GetProperty("CountedOrders").GetInt64());
        Assert.Equal(["PETR4/1/accepted=1", "VALE3/1/rejected=1"], ReadSentOrderCountsOf(orderCountsSentContexts[1]));
        Assert.Equal(2, orderCountsSentContexts[1].GetProperty("CountedOrders").GetInt64());
        Assert.Equal(string.Empty, orderCountsSentContexts[2].GetProperty("SentOrderCounts").GetString());
        Assert.Equal(0, orderCountsSentContexts[2].GetProperty("CountedOrders").GetInt64());
    }

    private static List<string> ReadSentOrderCountsOf(System.Text.Json.JsonElement orderCountsSentContext) =>
        orderCountsSentContext.GetProperty("SentOrderCounts").GetString()!.Split(' ').Order(StringComparer.Ordinal).ToList();

    [Fact]
    public async Task Orders_stored_before_the_worker_starts_are_not_counted()
    {
        // Arrange
        var flowaTestDatabase = await datadogMetricsDatabase.CreateFlowaDatabaseWithTheThreeExposuresAsync();
        await flowaTestDatabase.StoreAnsweredOrderAsync("PETR4", BuyOrderSide, accepted: true);
        await flowaTestDatabase.StoreAnsweredOrderAsync("VALE3", SellOrderSide, accepted: false);
        await using var datadogMetricsWorker = new DatadogMetricsTestWorker(flowaTestDatabase.FlowaConnectionString, dogStatsdUdpListener, TimeProvider.System);

        // Act
        var startupCycleOrderCounts = FilterAnsweredOrderCountLines(await datadogMetricsWorker.RunOneMetricsCycleAndReadSentMetricsAsync());
        var nextCycleOrderCounts = FilterAnsweredOrderCountLines(await datadogMetricsWorker.RunOneMetricsCycleAndReadSentMetricsAsync());

        // Assert
        Assert.Empty(startupCycleOrderCounts);
        Assert.Empty(nextCycleOrderCounts);
        Assert.Equal(
            ["Information Order count starts after the stored orders.", "Information Answered order counts sent."],
            datadogMetricsWorker.GetRecordingLoggerOf<SendAnsweredOrderCountsUseCase>().RecordedLogLines);
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
            Assert.Equal([BuildAcceptedOrderCountLine("1", "symbol:PETR4", "side:buy")],
                FilterAnsweredOrderCountLines(await workerBeforeTheRestart.RunOneMetricsCycleAndReadSentMetricsAsync()));
        }

        await using var workerAfterTheRestart = new DatadogMetricsTestWorker(flowaTestDatabase.FlowaConnectionString, dogStatsdUdpListener, TimeProvider.System);

        // Act
        var restartCycleOrderCounts = FilterAnsweredOrderCountLines(await workerAfterTheRestart.RunOneMetricsCycleAndReadSentMetricsAsync());
        await flowaTestDatabase.StoreAnsweredOrderAsync("VIIA4", SellOrderSide, accepted: true);
        var cycleAfterTheRestartCounts = FilterAnsweredOrderCountLines(await workerAfterTheRestart.RunOneMetricsCycleAndReadSentMetricsAsync());

        // Assert
        Assert.Empty(restartCycleOrderCounts);
        Assert.Equal([BuildAcceptedOrderCountLine("1", "symbol:VIIA4", "side:sell")], cycleAfterTheRestartCounts);
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
        var invalidOrderCounts = FilterAnsweredOrderCountLines(await datadogMetricsWorker.RunOneMetricsCycleAndReadSentMetricsAsync());

        // Assert
        Assert.Equal(
            DatadogMetricsTestWorker.SortMetricLinesForComparison([
                BuildRejectedOrderCountLine("1", "symbol:PETR4", "side:invalido"),
                BuildRejectedOrderCountLine("2", "symbol:invalido", "side:invalido")
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
        var cycleAfterTheDeleteCounts = FilterAnsweredOrderCountLines(await datadogMetricsWorker.RunOneMetricsCycleAndReadSentMetricsAsync());

        // Assert
        Assert.Equal([BuildAcceptedOrderCountLine("1", "symbol:VALE3", "side:buy")], cycleAfterTheDeleteCounts);
    }

    private static List<DogStatsdMetricLine> FilterAnsweredOrderCountLines(IEnumerable<DogStatsdMetricLine> sentMetrics) =>
        DatadogMetricsTestWorker.SortMetricLinesForComparison(sentMetrics.Where(sentMetric => sentMetric.MetricName.StartsWith("flowa.ordens.")));

    private static DogStatsdMetricLine BuildAcceptedOrderCountLine(string orderCount, params string[] orderTags) =>
        new("flowa.ordens.aceitas", orderCount, "c", DatadogMetricsTestWorker.BuildExpectedMetricTags(orderTags));

    private static DogStatsdMetricLine BuildRejectedOrderCountLine(string orderCount, params string[] orderTags) =>
        new("flowa.ordens.rejeitadas", orderCount, "c", DatadogMetricsTestWorker.BuildExpectedMetricTags(orderTags));

    public void Dispose() => dogStatsdUdpListener.Dispose();
}
