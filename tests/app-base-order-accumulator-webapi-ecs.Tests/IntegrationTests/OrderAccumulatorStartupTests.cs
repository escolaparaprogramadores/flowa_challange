using System.Diagnostics;
using System.Net;
using Base.OrderAccumulator.Infrastructure.Persistence;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace Base.OrderAccumulator.Tests;

// The app starts against its own empty database: the migration has to run at startup.
[Collection(OrderAccumulatorPostgresCollection.Name)]
public sealed class OrderAccumulatorStartupTests(OrderAccumulatorPostgresFixture orderAccumulatorDatabase) : IAsyncLifetime
{
    private string emptyOrderAccumulatorDatabaseConnectionString = null!;
    private WebApplicationFactory<Program> orderAccumulatorApp = null!;

    public async Task InitializeAsync()
    {
        var emptyOrderAccumulatorDatabaseName = "startup_" + Guid.NewGuid().ToString("N");
        await using (var postgresAdminConnection = await orderAccumulatorDatabase.OrderDatabaseDataSource.OpenConnectionAsync())
            await postgresAdminConnection.ExecuteAsync($"CREATE DATABASE {emptyOrderAccumulatorDatabaseName}");

        emptyOrderAccumulatorDatabaseConnectionString = new NpgsqlConnectionStringBuilder(orderAccumulatorDatabase.OrderDatabaseConnectionString)
        {
            Database = emptyOrderAccumulatorDatabaseName
        }.ConnectionString;
        orderAccumulatorApp = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(orderAccumulatorHost => orderAccumulatorHost
                .UseSetting("ConnectionStrings:Flowa", emptyOrderAccumulatorDatabaseConnectionString)
                // The FIX acceptor starts too: a free port and only on loopback, without competing for 9876 or opening to the network.
                .UseSetting("Fix:AcceptorPort", "0")
                .UseSetting("Fix:AcceptorBindHost", OrderAccumulatorFixTestHost.FixAcceptorLoopbackBindHost));
    }

    public async Task DisposeAsync() => await orderAccumulatorApp.DisposeAsync();

    [Fact]
    public async Task Health_answers_healthy()
    {
        var orderAccumulatorHealthResponse = await orderAccumulatorApp.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, orderAccumulatorHealthResponse.StatusCode);
        Assert.Equal("Healthy", await orderAccumulatorHealthResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Version_answers_the_commit_the_app_was_built_from()
    {
        var orderAccumulatorVersionResponse = await orderAccumulatorApp.CreateClient().GetAsync("/version");

        // Contract §1: {"commit":"<full sha, 40 characters>"}.
        var gitHeadSha = ReadGitHeadSha();
        Assert.Equal(40, gitHeadSha.Length);
        Assert.Equal(HttpStatusCode.OK, orderAccumulatorVersionResponse.StatusCode);
        Assert.Equal($$"""{"commit":"{{gitHeadSha}}"}""", await orderAccumulatorVersionResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Startup_creates_the_tables_and_one_zeroed_row_per_symbol()
    {
        orderAccumulatorApp.CreateClient();

        await using var orderDatabaseConnection = new NpgsqlConnection(emptyOrderAccumulatorDatabaseConnectionString);
        var symbolExposureRows = (await orderDatabaseConnection.QueryAsync<(string Symbol, decimal Exposure)>(
            "SELECT symbol, exposure FROM exposures ORDER BY symbol")).ToList();
        var storedOrderCount = await orderDatabaseConnection.ExecuteScalarAsync<long>("SELECT count(*) FROM orders");

        Assert.Equal([("PETR4", 0m), ("VALE3", 0m), ("VIIA4", 0m)], symbolExposureRows);
        Assert.Equal(0, storedOrderCount);
    }

    [Fact]
    public async Task Many_instances_applying_the_schema_at_once_on_an_empty_database_all_succeed()
    {
        await using var emptyOrderAccumulatorDatabase = NpgsqlDataSource.Create(emptyOrderAccumulatorDatabaseConnectionString);

        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => emptyOrderAccumulatorDatabase.ApplyOrderAccumulatorSchemaAsync())));

        await using var orderDatabaseConnection = await emptyOrderAccumulatorDatabase.OpenConnectionAsync();
        Assert.Equal(3, await orderDatabaseConnection.ExecuteScalarAsync<long>("SELECT count(*) FROM exposures"));
    }

    private static string ReadGitHeadSha()
    {
        using var gitRevParseProcess = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD")
        {
            RedirectStandardOutput = true,
            WorkingDirectory = AppContext.BaseDirectory
        })!;
        var gitHeadSha = gitRevParseProcess.StandardOutput.ReadToEnd().Trim();
        gitRevParseProcess.WaitForExit();
        return gitHeadSha;
    }
}
