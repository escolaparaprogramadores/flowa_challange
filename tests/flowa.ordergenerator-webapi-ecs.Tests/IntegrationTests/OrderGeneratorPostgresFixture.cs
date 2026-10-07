using Dapper;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Flowa.OrderGenerator.Tests;

// A real PostgreSQL, in a container, shared by the tests of the routes that read and delete in the database.
// The tables come from the Schema.sql of the OrderAccumulator, the owner of the schema (contract of F3 → F4).
public sealed class OrderGeneratorPostgresFixture : IAsyncLifetime
{
    private const string SeedSymbolExposuresSql = "INSERT INTO exposures (symbol) VALUES ('PETR4'), ('VALE3'), ('VIIA4')";
    private const string DropOrderTablesSql = "DROP TABLE IF EXISTS orders, exposures";

    private readonly PostgreSqlContainer _orderGeneratorPostgresContainer = new PostgreSqlBuilder("postgres:17").Build();

    public string OrderDatabaseConnectionString { get; private set; } = null!;
    public NpgsqlDataSource OrderDatabaseDataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _orderGeneratorPostgresContainer.StartAsync();
        OrderDatabaseConnectionString = _orderGeneratorPostgresContainer.GetConnectionString();
        OrderDatabaseDataSource = NpgsqlDataSource.Create(OrderDatabaseConnectionString);
    }

    public async Task DisposeAsync()
    {
        await OrderDatabaseDataSource.DisposeAsync();
        await _orderGeneratorPostgresContainer.DisposeAsync();
    }

    public async Task CreateEmptyOrderTablesAsync()
    {
        await using var orderDatabaseConnection = await OrderDatabaseDataSource.OpenConnectionAsync();
        await orderDatabaseConnection.ExecuteAsync(DropOrderTablesSql);
        await orderDatabaseConnection.ExecuteAsync(await File.ReadAllTextAsync(OrderAccumulatorSchemaPath()));
        await orderDatabaseConnection.ExecuteAsync(SeedSymbolExposuresSql);
    }

    public async Task DropOrderTablesAsync()
    {
        await using var orderDatabaseConnection = await OrderDatabaseDataSource.OpenConnectionAsync();
        await orderDatabaseConnection.ExecuteAsync(DropOrderTablesSql);
    }

    public async Task SetSymbolExposureAsync(string symbol, decimal symbolExposure)
    {
        await using var orderDatabaseConnection = await OrderDatabaseDataSource.OpenConnectionAsync();
        await orderDatabaseConnection.ExecuteAsync("UPDATE exposures SET exposure = @Exposure WHERE symbol = @Symbol", new { Symbol = symbol, Exposure = symbolExposure });
    }

    public async Task InsertStoredOrderAsync(StoredOrderTestRow storedOrder)
    {
        await using var orderDatabaseConnection = await OrderDatabaseDataSource.OpenConnectionAsync();
        await orderDatabaseConnection.ExecuteAsync(
            """
            INSERT INTO orders (cl_ord_id, order_id, exec_id, symbol, side, quantity, price, accepted, reject_reason, received_at)
            VALUES (@ClOrdId, @OrderId, @ExecId, @Symbol, @Side, @Quantity, @Price, @Accepted, @RejectReason, @ReceivedAt)
            """,
            storedOrder);
    }

    public async Task<long> CountStoredOrdersAsync()
    {
        await using var orderDatabaseConnection = await OrderDatabaseDataSource.OpenConnectionAsync();
        return await orderDatabaseConnection.ExecuteScalarAsync<long>("SELECT count(*) FROM orders");
    }

    public async Task<IReadOnlyList<decimal>> ReadSymbolExposuresInOrderAsync()
    {
        await using var orderDatabaseConnection = await OrderDatabaseDataSource.OpenConnectionAsync();
        var storedSymbolExposures = await orderDatabaseConnection.QueryAsync<decimal>(
            "SELECT exposure FROM exposures WHERE symbol = ANY(@Symbols) ORDER BY array_position(@Symbols, symbol)",
            new { Symbols = new[] { "PETR4", "VALE3", "VIIA4" } });
        return storedSymbolExposures.ToList();
    }

    // Holds the orders table locked until disposed, so a read of the OrderGenerator waits on it.
    public async Task<NpgsqlTransaction> LockOrderTableAsync()
    {
        var lockingConnection = await OrderDatabaseDataSource.OpenConnectionAsync();
        var lockingTransaction = await lockingConnection.BeginTransactionAsync();
        await lockingConnection.ExecuteAsync("LOCK TABLE orders IN ACCESS EXCLUSIVE MODE", transaction: lockingTransaction);
        return lockingTransaction;
    }

    private static string OrderAccumulatorSchemaPath()
    {
        var searchedFolder = new DirectoryInfo(AppContext.BaseDirectory);
        while (searchedFolder is not null && !File.Exists(Path.Combine(searchedFolder.FullName, "Flowa.slnx")))
            searchedFolder = searchedFolder.Parent;

        return Path.Combine(
            searchedFolder?.FullName ?? throw new InvalidOperationException("Flowa.slnx was not found above the test output."),
            "src", "flowa.orderaccumulator-worker-ecs", "Infrastructure", "Persistence", "Schema.sql");
    }
}

public sealed record StoredOrderTestRow(
    string ClOrdId, string OrderId, string ExecId, string? Symbol, string Side, decimal Quantity, decimal Price, bool Accepted,
    string? RejectReason, DateTime ReceivedAt);

[CollectionDefinition(Name)]
public sealed class OrderGeneratorPostgresCollection : ICollectionFixture<OrderGeneratorPostgresFixture>
{
    public const string Name = "orderGeneratorDatabase";
}
