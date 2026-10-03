using System.Net;
using System.Net.Sockets;
using System.Text;
using Dapper;
using Flowa.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using OrderAccumulator.Exposure;
using OrderAccumulator.Observabilidade;
using OrderAccumulator.Persistence;
using StatsdClient;

namespace OrderAccumulator.Tests;

// As métricas saem por UDP de verdade para um ouvinte local, no lugar do agente do Datadog,
// e o processamento usa o PostgreSQL real: aceita, rejeita e repetição vêm do banco.
[Collection(OrderAccumulatorPostgresCollection.Name)]
public sealed class OrderMetricsTests(OrderAccumulatorPostgresFixture orderAccumulatorDatabase) : IAsyncLifetime
{
    private const string SentinelMetricName = "flowa.teste.sentinela";
    private static readonly string[] UnifiedServiceTags = ["env:dev", "service:order-accumulator", "version:sha-de-teste"];

    private readonly DogStatsdUdpListener dogStatsdUdpListener = new();
    private DogStatsdService orderMetricsClient = null!;
    private SymbolExposureMemory symbolExposureMemory = null!;
    private OrderProcessorWithMetrics orderProcessorWithMetrics = null!;

    public async Task InitializeAsync()
    {
        await orderAccumulatorDatabase.ResetOrdersAndExposuresAsync();
        orderMetricsClient = OrderMetricsSetup.CreateOrderMetricsClient(dogStatsdUdpListener.ListenerPort, UnifiedServiceConfiguration());
        symbolExposureMemory = new SymbolExposureMemory();
        symbolExposureMemory.LoadStoredExposures(await orderAccumulatorDatabase.ExposureReader.GetSymbolExposuresAsync());
        orderProcessorWithMetrics = new OrderProcessorWithMetrics(orderAccumulatorDatabase.OrderProcessor, orderMetricsClient, symbolExposureMemory);
    }

    public Task DisposeAsync()
    {
        orderMetricsClient.Dispose();
        dogStatsdUdpListener.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Accepted_order_sends_one_accepted_count_with_symbol_side_and_unified_tags()
    {
        var acceptedOrderOutcome = await orderProcessorWithMetrics.ProcessIncomingOrderAsync(TestOrders.NewBuyOrder("PETR4", 100, 10.50m));

        var sentOrderMetrics = await SendSentinelAndReadOrderMetricsAsync();

        Assert.True(acceptedOrderOutcome.Accepted);
        var acceptedOrderMetric = Assert.Single(sentOrderMetrics);
        Assert.Equal(new DogStatsdMetricLine(OrderMetricNames.AcceptedOrders, "1", "c", ExpectedTags("symbol:PETR4", "side:buy")), acceptedOrderMetric);
    }

    [Fact]
    public async Task Order_over_the_exposure_limit_sends_one_rejected_count_with_symbol_and_side()
    {
        await SetStoredExposureAsync("VALE3", -ExposureLimit.PerSymbol);
        var rejectedOrderOutcome = await orderProcessorWithMetrics.ProcessIncomingOrderAsync(TestOrders.NewSellOrder("VALE3", 1, 0.01m));

        var sentOrderMetrics = await SendSentinelAndReadOrderMetricsAsync();

        Assert.False(rejectedOrderOutcome.Accepted);
        var rejectedOrderMetric = Assert.Single(sentOrderMetrics);
        Assert.Equal(new DogStatsdMetricLine(OrderMetricNames.RejectedOrders, "1", "c", ExpectedTags("symbol:VALE3", "side:sell")), rejectedOrderMetric);
    }

    [Fact]
    public async Task Repeated_clordid_is_counted_once_and_moves_the_exposure_memory_once()
    {
        var firstBuyOrder = TestOrders.NewBuyOrder("VIIA4", 200, 3.25m);
        await orderProcessorWithMetrics.ProcessIncomingOrderAsync(firstBuyOrder);
        var repeatedOrderOutcome = await orderProcessorWithMetrics.ProcessIncomingOrderAsync(firstBuyOrder);

        var sentOrderMetrics = await SendSentinelAndReadOrderMetricsAsync();

        Assert.True(repeatedOrderOutcome.IsRepeat);
        var acceptedOrderMetric = Assert.Single(sentOrderMetrics);
        Assert.Equal(new DogStatsdMetricLine(OrderMetricNames.AcceptedOrders, "1", "c", ExpectedTags("symbol:VIIA4", "side:buy")), acceptedOrderMetric);
        Assert.Equal(650m, ExposureInMemory("VIIA4"));
        Assert.Equal(650m, await orderAccumulatorDatabase.ReadExposureOfSymbolAsync("VIIA4"));
    }

    [Fact]
    public async Task Unknown_symbol_is_counted_as_invalid_symbol_and_side_never_as_the_typed_text()
    {
        await orderProcessorWithMetrics.ProcessIncomingOrderAsync(TestOrders.NewBuyOrder("ABCD3-texto-livre", 10, 1m));

        var sentOrderMetrics = await SendSentinelAndReadOrderMetricsAsync();

        var rejectedOrderMetric = Assert.Single(sentOrderMetrics);
        Assert.Equal(new DogStatsdMetricLine(OrderMetricNames.RejectedOrders, "1", "c", ExpectedTags("symbol:invalido", "side:invalido")), rejectedOrderMetric);
    }

    [Fact]
    public async Task Unknown_side_with_a_known_symbol_is_counted_as_invalid_side()
    {
        await orderProcessorWithMetrics.ProcessIncomingOrderAsync(TestOrders.NewIncomingOrder("PETR4", '3', 10, 1m));

        var sentOrderMetrics = await SendSentinelAndReadOrderMetricsAsync();

        var rejectedOrderMetric = Assert.Single(sentOrderMetrics);
        Assert.Equal(new DogStatsdMetricLine(OrderMetricNames.RejectedOrders, "1", "c", ExpectedTags("symbol:PETR4", "side:invalido")), rejectedOrderMetric);
    }

    [Fact]
    public async Task Order_tags_never_carry_the_clordid()
    {
        var acceptedSellOrder = TestOrders.NewSellOrder("PETR4", 5, 2m);
        await orderProcessorWithMetrics.ProcessIncomingOrderAsync(acceptedSellOrder);

        var sentOrderMetrics = await SendSentinelAndReadOrderMetricsAsync();

        var acceptedOrderMetric = Assert.Single(sentOrderMetrics);
        Assert.Equal(ExpectedTags("symbol:PETR4", "side:sell"), acceptedOrderMetric.MetricTags);
        Assert.DoesNotContain(acceptedOrderMetric.MetricTags, metricTag => metricTag.Contains(acceptedSellOrder.ClOrdId));
    }

    [Fact]
    public async Task Failed_processing_is_not_counted_and_the_same_exception_reaches_the_caller()
    {
        var databaseFailure = new NpgsqlException("banco fora do ar");
        var orderProcessorWithFailingDatabase = new OrderProcessorWithMetrics(
            new OrderProcessorFailingWith(databaseFailure), orderMetricsClient, symbolExposureMemory);

        var thrownFailure = await Assert.ThrowsAsync<NpgsqlException>(() =>
            orderProcessorWithFailingDatabase.ProcessIncomingOrderAsync(TestOrders.NewBuyOrder("PETR4", 1, 1m)));
        var sentOrderMetrics = await SendSentinelAndReadOrderMetricsAsync();

        Assert.Same(databaseFailure, thrownFailure);
        Assert.Empty(sentOrderMetrics);
        Assert.Equal(0m, ExposureInMemory("PETR4"));
    }

    [Fact]
    public async Task Exposure_gauge_sends_the_three_symbols_from_memory_after_accepted_buys_and_sells()
    {
        await orderProcessorWithMetrics.ProcessIncomingOrderAsync(TestOrders.NewBuyOrder("PETR4", 1000, 12.34m));
        await orderProcessorWithMetrics.ProcessIncomingOrderAsync(TestOrders.NewSellOrder("VALE3", 300, 50.10m));
        await SendSentinelAndReadOrderMetricsAsync();

        new SymbolExposureGaugeService(orderMetricsClient, symbolExposureMemory).SendSymbolExposureGauges();
        var sentExposureGauges = await SendSentinelAndReadOrderMetricsAsync();

        Assert.Equal(
            [
                new DogStatsdMetricLine(OrderMetricNames.SymbolExposure, "12340", "g", ExpectedTags("symbol:PETR4")),
                new DogStatsdMetricLine(OrderMetricNames.SymbolExposure, "-15030", "g", ExpectedTags("symbol:VALE3")),
                new DogStatsdMetricLine(OrderMetricNames.SymbolExposure, "0", "g", ExpectedTags("symbol:VIIA4"))
            ],
            sentExposureGauges.OrderBy(exposureGauge => exposureGauge.MetricTags.Single(metricTag => metricTag.StartsWith("symbol:"))));
    }

    [Fact]
    public void Exposure_gauge_is_resent_every_30_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), SymbolExposureGaugeService.SymbolExposureGaugeInterval);
    }

    [Fact]
    public async Task Without_an_agent_listening_orders_are_still_processed_without_errors()
    {
        int portWithoutAgent;
        using (var closedUdpPort = new UdpClient(0))
            portWithoutAgent = ((IPEndPoint)closedUdpPort.Client.LocalEndPoint!).Port;
        using var orderMetricsClientWithoutAgent = OrderMetricsSetup.CreateOrderMetricsClient(portWithoutAgent, UnifiedServiceConfiguration());
        var orderProcessorWithoutAgent = new OrderProcessorWithMetrics(
            orderAccumulatorDatabase.OrderProcessor, orderMetricsClientWithoutAgent, new SymbolExposureMemory());

        var orderOutcomesWithoutAgent = new List<OrderOutcome>();
        for (var orderNumber = 0; orderNumber < 20; orderNumber++)
        {
            orderOutcomesWithoutAgent.Add(await orderProcessorWithoutAgent.ProcessIncomingOrderAsync(TestOrders.NewBuyOrder("PETR4", 1, 1m)));
            orderMetricsClientWithoutAgent.Flush();
        }

        Assert.Equal(20, orderOutcomesWithoutAgent.Count(orderOutcome => orderOutcome.Accepted));
        Assert.Equal(20m, await orderAccumulatorDatabase.ReadExposureOfSymbolAsync("PETR4"));
    }

    [Fact]
    public async Task App_wraps_the_postgres_processor_and_loads_the_stored_exposure_at_startup()
    {
        var startupDatabaseName = "metricas_" + Guid.NewGuid().ToString("N");
        await using (var postgresAdminConnection = await orderAccumulatorDatabase.OrderDatabaseDataSource.OpenConnectionAsync())
            await postgresAdminConnection.ExecuteAsync($"CREATE DATABASE {startupDatabaseName}");
        var startupDatabaseConnectionString = new NpgsqlConnectionStringBuilder(orderAccumulatorDatabase.OrderDatabaseConnectionString)
        {
            Database = startupDatabaseName
        }.ConnectionString;
        await using (var startupDatabase = NpgsqlDataSource.Create(startupDatabaseConnectionString))
        {
            await startupDatabase.ApplyOrderAccumulatorSchemaAsync();
            await using var startupDatabaseConnection = await startupDatabase.OpenConnectionAsync();
            await startupDatabaseConnection.ExecuteAsync("UPDATE exposures SET exposure = 4321.50 WHERE symbol = 'VALE3'");
        }

        await using var orderAccumulatorApp = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(orderAccumulatorHost => orderAccumulatorHost
                .UseSetting("ConnectionStrings:Flowa", startupDatabaseConnectionString)
                .UseSetting("Fix:AcceptorPort", "0")
                .UseSetting("Fix:AcceptorBindHost", OrderAccumulatorFixTestHost.FixAcceptorLoopbackBindHost));
        orderAccumulatorApp.CreateClient();

        var appServices = orderAccumulatorApp.Services;
        Assert.IsType<OrderProcessorWithMetrics>(appServices.GetRequiredService<IOrderProcessor>());
        Assert.Contains(appServices.GetServices<IHostedService>(), hostedService => hostedService is SymbolExposureGaugeService);
        Assert.Equal(
            [new SymbolExposure("PETR4", 0m), new SymbolExposure("VALE3", 4321.50m), new SymbolExposure("VIIA4", 0m)],
            appServices.GetRequiredService<SymbolExposureMemory>().CurrentSymbolExposures());
    }

    private decimal ExposureInMemory(string orderSymbol) =>
        symbolExposureMemory.CurrentSymbolExposures().Single(currentSymbolExposure => currentSymbolExposure.Symbol == orderSymbol).Exposure;

    private async Task SetStoredExposureAsync(string orderSymbol, decimal storedExposure)
    {
        await using (var orderDatabaseConnection = await orderAccumulatorDatabase.OrderDatabaseDataSource.OpenConnectionAsync())
            await orderDatabaseConnection.ExecuteAsync(
                "UPDATE exposures SET exposure = @StoredExposure WHERE symbol = @Symbol", new { StoredExposure = storedExposure, Symbol = orderSymbol });
        symbolExposureMemory.LoadStoredExposures(await orderAccumulatorDatabase.ExposureReader.GetSymbolExposuresAsync());
    }

    // A ausência de métrica só se prova com um envio depois: tudo o que chegou antes da sentinela é o que foi enviado.
    private async Task<List<DogStatsdMetricLine>> SendSentinelAndReadOrderMetricsAsync()
    {
        orderMetricsClient.Flush();
        orderMetricsClient.Increment(SentinelMetricName);
        orderMetricsClient.Flush();
        var receivedFlowaMetrics = await dogStatsdUdpListener.ReadFlowaMetricsUntilAsync(SentinelMetricName);
        return receivedFlowaMetrics.Where(receivedFlowaMetric => receivedFlowaMetric.MetricName != SentinelMetricName).ToList();
    }

    private static IReadOnlySet<string> ExpectedTags(params string[] orderTags) =>
        new SortedSet<string>(UnifiedServiceTags.Concat(orderTags));

    private static IConfiguration UnifiedServiceConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DD_ENV"] = "dev",
                ["DD_SERVICE"] = "order-accumulator",
                ["DD_VERSION"] = "sha-de-teste"
            })
            .Build();

    private sealed class OrderProcessorFailingWith(Exception processingFailure) : IOrderProcessor
    {
        public Task<OrderOutcome> ProcessIncomingOrderAsync(IncomingOrder incomingOrder, CancellationToken cancellationToken = default) =>
            Task.FromException<OrderOutcome>(processingFailure);
    }
}

// Uma linha DogStatsD: "nome:valor|tipo|#etiqueta,etiqueta". As etiquetas viram conjunto ordenado
// para a comparação não depender da ordem em que o cliente as escreve.
public sealed record DogStatsdMetricLine(string MetricName, string MetricValue, string MetricType, IReadOnlySet<string> MetricTags)
{
    public static DogStatsdMetricLine ParseDogStatsdLine(string dogStatsdLine)
    {
        var dogStatsdFields = dogStatsdLine.Split('|');
        var metricNameAndValue = dogStatsdFields[0].Split(':', 2);
        var metricTagsField = dogStatsdFields.Skip(2).FirstOrDefault(dogStatsdField => dogStatsdField.StartsWith('#'));
        var metricTags = metricTagsField is null ? [] : metricTagsField[1..].Split(',');
        return new DogStatsdMetricLine(metricNameAndValue[0], metricNameAndValue[1], dogStatsdFields[1], new SortedSet<string>(metricTags));
    }

    public bool Equals(DogStatsdMetricLine? otherMetricLine) =>
        otherMetricLine is not null
        && MetricName == otherMetricLine.MetricName
        && MetricValue == otherMetricLine.MetricValue
        && MetricType == otherMetricLine.MetricType
        && MetricTags.SetEquals(otherMetricLine.MetricTags);

    public override int GetHashCode() => HashCode.Combine(MetricName, MetricValue, MetricType);

    public override string ToString() => $"{MetricName}:{MetricValue}|{MetricType}|#{string.Join(',', MetricTags)}";
}

// Faz o papel do agente: escuta UDP em IPv4 e IPv6, porque "localhost" pode resolver para qualquer um.
public sealed class DogStatsdUdpListener : IDisposable
{
    private static readonly TimeSpan SentinelWaitLimit = TimeSpan.FromSeconds(15);
    private readonly UdpClient dogStatsdUdpClient;

    public DogStatsdUdpListener()
    {
        dogStatsdUdpClient = new UdpClient(AddressFamily.InterNetworkV6);
        dogStatsdUdpClient.Client.DualMode = true;
        dogStatsdUdpClient.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
    }

    public int ListenerPort => ((IPEndPoint)dogStatsdUdpClient.Client.LocalEndPoint!).Port;

    public async Task<List<DogStatsdMetricLine>> ReadFlowaMetricsUntilAsync(string sentinelMetricName)
    {
        using var sentinelWait = new CancellationTokenSource(SentinelWaitLimit);
        var receivedFlowaMetrics = new List<DogStatsdMetricLine>();
        while (!receivedFlowaMetrics.Any(receivedFlowaMetric => receivedFlowaMetric.MetricName == sentinelMetricName))
        {
            var receivedDatagram = await dogStatsdUdpClient.ReceiveAsync(sentinelWait.Token);
            receivedFlowaMetrics.AddRange(Encoding.UTF8.GetString(receivedDatagram.Buffer)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(dogStatsdLine => dogStatsdLine.StartsWith("flowa."))
                .Select(DogStatsdMetricLine.ParseDogStatsdLine));
        }

        return receivedFlowaMetrics;
    }

    public void Dispose() => dogStatsdUdpClient.Dispose();
}
