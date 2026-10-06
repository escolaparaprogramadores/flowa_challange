using Dapper;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Flowa.DatadogMetrics.Tests;

// A real PostgreSQL in a container. Each test asks for its own database, so the order ids and the
// exposure rows of one test never reach another.
public sealed class DatadogMetricsPostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer flowaPostgresContainer = new PostgreSqlBuilder("postgres:17").Build();

    private NpgsqlDataSource postgresAdminDataSource = null!;

    public async Task InitializeAsync()
    {
        await flowaPostgresContainer.StartAsync();
        postgresAdminDataSource = NpgsqlDataSource.Create(flowaPostgresContainer.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        await postgresAdminDataSource.DisposeAsync();
        await flowaPostgresContainer.DisposeAsync();
    }

    public async Task<FlowaTestDatabase> CreateEmptyFlowaDatabaseAsync()
    {
        var flowaDatabaseName = "metricas_" + Guid.NewGuid().ToString("N");
        await using (var postgresAdminConnection = await postgresAdminDataSource.OpenConnectionAsync())
            await postgresAdminConnection.ExecuteAsync($"CREATE DATABASE {flowaDatabaseName}");

        return new FlowaTestDatabase(new NpgsqlConnectionStringBuilder(flowaPostgresContainer.GetConnectionString())
        {
            Database = flowaDatabaseName
        }.ConnectionString);
    }

    public async Task<FlowaTestDatabase> CreateFlowaDatabaseWithTheThreeExposuresAsync(
        decimal petr4Exposure = 0m, decimal vale3Exposure = 0m, decimal viia4Exposure = 0m)
    {
        var flowaTestDatabase = await CreateEmptyFlowaDatabaseAsync();
        await flowaTestDatabase.ApplyAccumulatorSchemaAsync();
        await flowaTestDatabase.StoreExposureAsync("PETR4", petr4Exposure);
        await flowaTestDatabase.StoreExposureAsync("VALE3", vale3Exposure);
        await flowaTestDatabase.StoreExposureAsync("VIIA4", viia4Exposure);
        return flowaTestDatabase;
    }
}

[CollectionDefinition(Name)]
public sealed class DatadogMetricsPostgresCollection : ICollectionFixture<DatadogMetricsPostgresFixture>
{
    public const string Name = "datadogMetricsDatabase";
}

// The tables and rows the Accumulator writes, written straight with SQL: the worker only reads them.
public sealed class FlowaTestDatabase(string flowaConnectionString)
{
    public const string BuyOrderSide = "1";
    public const string SellOrderSide = "2";

    public string FlowaConnectionString { get; } = flowaConnectionString;

    public async Task ApplyAccumulatorSchemaAsync()
    {
        var accumulatorSchemaSql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Schema.sql"));
        await using var flowaConnection = await OpenFlowaConnectionAsync();
        await flowaConnection.ExecuteAsync(accumulatorSchemaSql);
    }

    public async Task StoreExposureAsync(string symbol, decimal exposure)
    {
        await using var flowaConnection = await OpenFlowaConnectionAsync();
        await flowaConnection.ExecuteAsync(
            "INSERT INTO exposures (symbol, exposure) VALUES (@Symbol, @Exposure) ON CONFLICT (symbol) DO UPDATE SET exposure = @Exposure",
            new { Symbol = symbol, Exposure = exposure });
    }

    public async Task StoreAnsweredOrderAsync(string? symbol, string side, bool accepted)
    {
        await using var flowaConnection = await OpenFlowaConnectionAsync();
        await flowaConnection.ExecuteAsync(
            """
            INSERT INTO orders (cl_ord_id, order_id, exec_id, symbol, side, quantity, price, accepted, reject_reason)
            VALUES (@ClOrdId, @OrderId, @ExecId, @Symbol, @Side, 10, 1.50, @Accepted, @RejectReason)
            """,
            new
            {
                ClOrdId = Guid.NewGuid().ToString("N"),
                OrderId = Guid.NewGuid().ToString("N"),
                ExecId = Guid.NewGuid().ToString("N"),
                Symbol = symbol,
                Side = side,
                Accepted = accepted,
                RejectReason = accepted ? null : "Ordem rejeitada."
            });
    }

    public async Task DeleteAllStoredOrdersAsync()
    {
        await using var flowaConnection = await OpenFlowaConnectionAsync();
        await flowaConnection.ExecuteAsync("DELETE FROM orders");
    }

    private async Task<NpgsqlConnection> OpenFlowaConnectionAsync()
    {
        var flowaConnection = new NpgsqlConnection(FlowaConnectionString);
        await flowaConnection.OpenAsync();
        return flowaConnection;
    }
}
