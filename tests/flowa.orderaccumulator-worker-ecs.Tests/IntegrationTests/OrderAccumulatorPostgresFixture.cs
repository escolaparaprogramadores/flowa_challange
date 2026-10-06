using Flowa.OrderAccumulator.Application.Exposures.Interfaces;
using Flowa.OrderAccumulator.Application.Orders.Responses;
using Flowa.OrderAccumulator.Commons.Database;
using Flowa.OrderAccumulator.Domain.Exposures.ValueObjects;
using Flowa.OrderAccumulator.Domain.Orders.Enums;
using Flowa.OrderAccumulator.Domain.Orders.ValueObjects;
using Flowa.OrderAccumulator.Infrastructure.DependencyInjection;
using Flowa.OrderAccumulator.Infrastructure.Exposures.Repositories;
using Dapper;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Flowa.OrderAccumulator.Tests;

// A real PostgreSQL, in a container, shared by the database tests.
public sealed class OrderAccumulatorPostgresFixture : IAsyncLifetime
{
    // max_connections above the default (100) so the concurrency test can open 200 connections at once.
    private readonly PostgreSqlContainer orderAccumulatorPostgresContainer = new PostgreSqlBuilder("postgres:17")
        .WithCommand("-c", "max_connections=300")
        .Build();

    // With the machine under load, a round of 200 orders already went past the Npgsql default of 30 s
    // waiting for the exposure row. The test gives more room; the production code does not change.
    public const int OrderDatabaseTestCommandTimeoutSeconds = 120;

    public string OrderDatabaseConnectionString { get; private set; } = null!;
    public NpgsqlDataSource OrderDatabaseDataSource { get; private set; } = null!;
    public PostgresConnectionSource OrderDatabaseConnectionSource { get; private set; } = null!;
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
        OrderDatabaseConnectionSource = new PostgresConnectionSource(OrderDatabaseConnectionString);
        await OrderDatabaseConnectionSource.ApplyOrderAccumulatorSchemaAsync();

        OrderDecisionRunner = new DecideIncomingOrderTestRunner(OrderDatabaseConnectionSource);
        ExposureReader = new SymbolExposureReaderWithOwnConnection(OrderDatabaseConnectionSource);
    }

    public async Task DisposeAsync()
    {
        await OrderDatabaseDataSource.DisposeAsync();
        await OrderDatabaseConnectionSource.DisposeAsync();
        await orderAccumulatorPostgresContainer.DisposeAsync();
    }

    // Each test starts from zero: no order and the three symbols zeroed by the migration itself.
    public async Task ResetOrdersAndExposuresAsync()
    {
        await using (var orderDatabaseConnection = await OrderDatabaseDataSource.OpenConnectionAsync())
            await orderDatabaseConnection.ExecuteAsync("TRUNCATE orders, exposures");

        await OrderDatabaseConnectionSource.ApplyOrderAccumulatorSchemaAsync();
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

    // Sums, straight from the orders table, price × quantity of the accepted ones (a buy adds, a sell subtracts).
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

    public static decimal ExposureDeltaOf(DecideIncomingOrderResponse orderDecision) =>
        ExposureLimitPolicy.CalculateOrderExposureDelta(
            orderDecision.Side == OrderSideCodes.BuyOrderSideFixCode ? OrderSide.Buy : OrderSide.Sell,
            (int)orderDecision.Quantity, orderDecision.Price);
}

// Reads the exposure on a connection of its own per call, as the read repository did before it moved to the
// connection of the operation scope: a test can read while other orders use their own connections.
public sealed class SymbolExposureReaderWithOwnConnection(IDatabaseConnectionSource orderDatabaseConnectionSource) : ISymbolExposureReadRepository
{
    public async Task<IReadOnlyList<SymbolExposure>> GetSymbolExposuresAsync(CancellationToken cancellationToken = default)
    {
        await using var exposureReadUnitOfWork = new DatabaseUnitOfWork(orderDatabaseConnectionSource);
        return await new SymbolExposureReadRepository(new DapperDatabase(exposureReadUnitOfWork)).GetSymbolExposuresAsync(cancellationToken);
    }
}
