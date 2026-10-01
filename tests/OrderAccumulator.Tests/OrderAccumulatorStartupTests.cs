using System.Diagnostics;
using System.Net;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using OrderAccumulator.Persistence;

namespace OrderAccumulator.Tests;

// O app sobe contra um banco vazio próprio: a migração tem de rodar na subida.
[Collection(OrderAccumulatorPostgresCollection.Name)]
public sealed class OrderAccumulatorStartupTests(OrderAccumulatorPostgresFixture orderAccumulatorDatabase) : IAsyncLifetime
{
    private string emptyDatabaseConnectionString = null!;
    private WebApplicationFactory<Program> orderAccumulatorApp = null!;

    public async Task InitializeAsync()
    {
        var emptyDatabaseName = "startup_" + Guid.NewGuid().ToString("N");
        await using (var adminConnection = await orderAccumulatorDatabase.OrderDatabaseDataSource.OpenConnectionAsync())
            await adminConnection.ExecuteAsync($"CREATE DATABASE {emptyDatabaseName}");

        emptyDatabaseConnectionString = new NpgsqlConnectionStringBuilder(orderAccumulatorDatabase.OrderDatabaseConnectionString)
        {
            Database = emptyDatabaseName
        }.ConnectionString;
        orderAccumulatorApp = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(orderAccumulatorHost => orderAccumulatorHost.UseSetting("ConnectionStrings:Flowa", emptyDatabaseConnectionString));
    }

    public async Task DisposeAsync() => await orderAccumulatorApp.DisposeAsync();

    [Fact]
    public async Task Health_answers_healthy()
    {
        var healthResponse = await orderAccumulatorApp.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);
        Assert.Equal("Healthy", await healthResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Version_answers_the_commit_the_app_was_built_from()
    {
        var versionResponse = await orderAccumulatorApp.CreateClient().GetAsync("/version");

        // Contrato §1: {"commit":"<sha completo, 40 caracteres>"}.
        var gitHeadSha = ReadGitHeadSha();
        Assert.Equal(40, gitHeadSha.Length);
        Assert.Equal(HttpStatusCode.OK, versionResponse.StatusCode);
        Assert.Equal($$"""{"commit":"{{gitHeadSha}}"}""", await versionResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Startup_creates_the_tables_and_one_zeroed_row_per_symbol()
    {
        orderAccumulatorApp.CreateClient();

        await using var orderDatabaseConnection = new NpgsqlConnection(emptyDatabaseConnectionString);
        var symbolExposureRows = (await orderDatabaseConnection.QueryAsync<(string Symbol, decimal Exposure)>(
            "SELECT symbol, exposure FROM exposures ORDER BY symbol")).ToList();
        var storedOrderCount = await orderDatabaseConnection.ExecuteScalarAsync<long>("SELECT count(*) FROM orders");

        Assert.Equal([("PETR4", 0m), ("VALE3", 0m), ("VIIA4", 0m)], symbolExposureRows);
        Assert.Equal(0, storedOrderCount);
    }

    [Fact]
    public async Task Many_instances_applying_the_schema_at_once_on_an_empty_database_all_succeed()
    {
        await using var emptyDatabase = NpgsqlDataSource.Create(emptyDatabaseConnectionString);

        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => emptyDatabase.ApplyOrderAccumulatorSchemaAsync())));

        await using var orderDatabaseConnection = await emptyDatabase.OpenConnectionAsync();
        Assert.Equal(3, await orderDatabaseConnection.ExecuteScalarAsync<long>("SELECT count(*) FROM exposures"));
    }

    private static string ReadGitHeadSha()
    {
        using var gitProcess = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD")
        {
            RedirectStandardOutput = true,
            WorkingDirectory = AppContext.BaseDirectory
        })!;
        var gitHeadSha = gitProcess.StandardOutput.ReadToEnd().Trim();
        gitProcess.WaitForExit();
        return gitHeadSha;
    }
}
