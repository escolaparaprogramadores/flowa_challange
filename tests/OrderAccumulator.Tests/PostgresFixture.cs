using Dapper;
using Flowa.Shared;
using Npgsql;
using OrderAccumulator.Exposure;
using OrderAccumulator.Persistence;
using Testcontainers.PostgreSql;

namespace OrderAccumulator.Tests;

// Um PostgreSQL de verdade, em container, compartilhado pelos testes de banco.
public sealed class PostgresFixture : IAsyncLifetime
{
    // max_connections acima do padrão (100) para o teste de concorrência abrir 200 conexões de uma vez.
    private readonly PostgreSqlContainer postgresContainer = new PostgreSqlBuilder("postgres:17")
        .WithCommand("-c", "max_connections=300")
        .Build();

    public string ConnectionString { get; private set; } = null!;
    public NpgsqlDataSource DataSource { get; private set; } = null!;
    public IOrderProcessor OrderProcessor { get; private set; } = null!;
    public IExposureReader ExposureReader { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await postgresContainer.StartAsync();

        ConnectionString = new NpgsqlConnectionStringBuilder(postgresContainer.GetConnectionString())
        {
            MaxPoolSize = 250
        }.ConnectionString;
        DataSource = NpgsqlDataSource.Create(ConnectionString);
        await DataSource.ApplySchemaAsync();

        OrderProcessor = new PostgresOrderProcessor(DataSource);
        ExposureReader = new PostgresExposureReader(DataSource);
    }

    public async Task DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await postgresContainer.DisposeAsync();
    }

    // Cada teste começa do zero: nenhuma ordem e os três símbolos zerados pela própria migração.
    public async Task ResetOrdersAndExposuresAsync()
    {
        await using (var connection = await DataSource.OpenConnectionAsync())
            await connection.ExecuteAsync("TRUNCATE orders, exposures");

        await DataSource.ApplySchemaAsync();
    }

    public async Task<decimal> ReadExposureOfSymbolAsync(string symbol) =>
        (await ExposureReader.GetSymbolExposuresAsync()).Single(symbolExposure => symbolExposure.Symbol == symbol).Exposure;

    public async Task<long> CountStoredOrdersAsync(string? clOrdId = null)
    {
        await using var connection = await DataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM orders WHERE @ClOrdId::text IS NULL OR cl_ord_id = @ClOrdId",
            new { ClOrdId = clOrdId });
    }

    // Soma, direto da tabela de ordens, preço × quantidade das aceitas (compra soma, venda subtrai).
    public async Task<decimal> SumAcceptedOrdersExposureAsync(string symbol)
    {
        await using var connection = await DataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<decimal>(
            """
            SELECT coalesce(sum(CASE WHEN side = @Buy THEN price * quantity ELSE -(price * quantity) END), 0)
            FROM orders
            WHERE symbol = @Symbol AND accepted
            """,
            new { Symbol = symbol, Buy = OrderSideCodes.BuyOrderSideFixCode.ToString() });
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

public static class TestOrders
{
    public static IncomingOrder NewIncomingOrder(string symbol, char side, decimal quantity, decimal price) =>
        new(Guid.NewGuid().ToString("N"), symbol, side, quantity, price);

    public static IncomingOrder NewBuyOrder(string symbol, decimal quantity, decimal price) =>
        NewIncomingOrder(symbol, OrderSideCodes.BuyOrderSideFixCode, quantity, price);

    public static IncomingOrder NewSellOrder(string symbol, decimal quantity, decimal price) =>
        NewIncomingOrder(symbol, OrderSideCodes.SellOrderSideFixCode, quantity, price);

    public static decimal ExposureDeltaOf(OrderOutcome orderOutcome) =>
        ExposureLimit.OrderExposureDelta(
            orderOutcome.Side == OrderSideCodes.BuyOrderSideFixCode ? OrderSide.Buy : OrderSide.Sell,
            (int)orderOutcome.Quantity, orderOutcome.Price);
}
