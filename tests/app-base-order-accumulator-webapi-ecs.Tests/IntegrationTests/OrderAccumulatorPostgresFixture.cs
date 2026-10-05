using Base.OrderAccumulator.Application.Exposures.GetExposures;
using Base.OrderAccumulator.Application.Orders.DecideIncomingOrder;
using Base.OrderAccumulator.Domain.Exposures;
using Base.OrderAccumulator.Domain.Orders;
using Base.OrderAccumulator.Infrastructure.Persistence;
using Dapper;
using Flowa.Shared;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Base.OrderAccumulator.Tests;

// Um PostgreSQL de verdade, em container, compartilhado pelos testes de banco.
public sealed class OrderAccumulatorPostgresFixture : IAsyncLifetime
{
    // max_connections acima do padrão (100) para o teste de concorrência abrir 200 conexões de uma vez.
    private readonly PostgreSqlContainer orderAccumulatorPostgresContainer = new PostgreSqlBuilder("postgres:17")
        .WithCommand("-c", "max_connections=300")
        .Build();

    // Com a máquina carregada, uma rodada de 200 ordens já passou dos 30 s padrão do Npgsql
    // esperando a linha de exposição. O teste dá mais folga; o código de produção não muda.
    public const int OrderDatabaseTestCommandTimeoutSeconds = 120;

    public string OrderDatabaseConnectionString { get; private set; } = null!;
    public NpgsqlDataSource OrderDatabaseDataSource { get; private set; } = null!;
    public DecideIncomingOrderTestRunner OrderDecisionRunner { get; private set; } = null!;
    public ISymbolExposureReadRepository ExposureReader { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await orderAccumulatorPostgresContainer.StartAsync();

        OrderDatabaseConnectionString = new NpgsqlConnectionStringBuilder(orderAccumulatorPostgresContainer.GetConnectionString())
        {
            MaxPoolSize = 250,
            CommandTimeout = OrderDatabaseTestCommandTimeoutSeconds
        }.ConnectionString;
        OrderDatabaseDataSource = NpgsqlDataSource.Create(OrderDatabaseConnectionString);
        await OrderDatabaseDataSource.ApplyOrderAccumulatorSchemaAsync();

        OrderDecisionRunner = new DecideIncomingOrderTestRunner(OrderDatabaseDataSource);
        ExposureReader = new SymbolExposureReadRepository(OrderDatabaseDataSource);
    }

    public async Task DisposeAsync()
    {
        await OrderDatabaseDataSource.DisposeAsync();
        await orderAccumulatorPostgresContainer.DisposeAsync();
    }

    // Cada teste começa do zero: nenhuma ordem e os três símbolos zerados pela própria migração.
    public async Task ResetOrdersAndExposuresAsync()
    {
        await using (var orderDatabaseConnection = await OrderDatabaseDataSource.OpenConnectionAsync())
            await orderDatabaseConnection.ExecuteAsync("TRUNCATE orders, exposures");

        await OrderDatabaseDataSource.ApplyOrderAccumulatorSchemaAsync();
    }

    public async Task<decimal> ReadExposureOfSymbolAsync(string symbol) =>
        (await ExposureReader.GetSymbolExposuresAsync()).Single(symbolExposure => symbolExposure.Symbol == symbol).Exposure;

    public async Task<long> CountStoredOrdersAsync(string? clOrdId = null)
    {
        await using var orderDatabaseConnection = await OrderDatabaseDataSource.OpenConnectionAsync();
        return await orderDatabaseConnection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM orders WHERE @ClOrdId::text IS NULL OR cl_ord_id = @ClOrdId",
            new { ClOrdId = clOrdId });
    }

    // Soma, direto da tabela de ordens, preço × quantidade das aceitas (compra soma, venda subtrai).
    public async Task<decimal> SumAcceptedOrdersExposureAsync(string symbol)
    {
        await using var orderDatabaseConnection = await OrderDatabaseDataSource.OpenConnectionAsync();
        return await orderDatabaseConnection.ExecuteScalarAsync<decimal>(
            """
            SELECT coalesce(sum(CASE WHEN side = @Buy THEN price * quantity ELSE -(price * quantity) END), 0)
            FROM orders
            WHERE symbol = @Symbol AND accepted
            """,
            new { Symbol = symbol, Buy = OrderSideCodes.BuyOrderSideFixCode.ToString() });
    }
}

[CollectionDefinition(Name)]
public sealed class OrderAccumulatorPostgresCollection : ICollectionFixture<OrderAccumulatorPostgresFixture>
{
    public const string Name = "orderAccumulatorDatabase";
}

public static class TestOrders
{
    public static IncomingOrder NewIncomingOrder(string symbol, char side, decimal quantity, decimal price) =>
        new(Guid.NewGuid().ToString("N"), symbol, side, quantity, price);

    public static IncomingOrder NewBuyOrder(string symbol, decimal quantity, decimal price) =>
        NewIncomingOrder(symbol, OrderSideCodes.BuyOrderSideFixCode, quantity, price);

    public static IncomingOrder NewSellOrder(string symbol, decimal quantity, decimal price) =>
        NewIncomingOrder(symbol, OrderSideCodes.SellOrderSideFixCode, quantity, price);

    public static decimal ExposureDeltaOf(DecideIncomingOrderOutput orderDecision) =>
        ExposureLimitPolicy.CalculateOrderExposureDelta(
            orderDecision.Side == OrderSideCodes.BuyOrderSideFixCode ? OrderSide.Buy : OrderSide.Sell,
            (int)orderDecision.Quantity, orderDecision.Price);
}
