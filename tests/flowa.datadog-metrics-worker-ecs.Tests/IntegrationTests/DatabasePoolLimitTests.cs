using Flowa.Commons.Database;
using Flowa.DatadogMetrics.Infrastructure.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Flowa.DatadogMetrics.Tests;

// CA-32: the Database:MaximumPoolSize the worker reads is the limit of the connections it really opens; without the key
// the connection keeps the limit written in the connection string.
[Collection(DatadogMetricsPostgresCollection.Name)]
public sealed class DatabasePoolLimitTests(DatadogMetricsPostgresFixture datadogMetricsDatabase)
{
    [Fact]
    public async Task Connections_the_worker_opens_carry_the_maximum_pool_size_of_the_key()
    {
        // Arrange
        var flowaTestDatabase = await datadogMetricsDatabase.CreateEmptyFlowaDatabaseAsync();

        // Act
        var maximumPoolSizeOfAWorkerConnection = await ReadMaximumPoolSizeOfAWorkerConnectionAsync(flowaTestDatabase, "2");

        // Assert
        Assert.Equal(2, maximumPoolSizeOfAWorkerConnection);
    }

    [Fact]
    public async Task Without_the_key_the_worker_connections_keep_the_limit_of_the_connection_string()
    {
        // Arrange
        var flowaTestDatabase = await datadogMetricsDatabase.CreateEmptyFlowaDatabaseAsync();
        var maximumPoolSizeOfTheConnectionString = new NpgsqlConnectionStringBuilder(flowaTestDatabase.FlowaConnectionString).MaxPoolSize;

        // Act
        var maximumPoolSizeOfAWorkerConnection = await ReadMaximumPoolSizeOfAWorkerConnectionAsync(flowaTestDatabase, configuredMaximumPoolSize: null);

        // Assert
        Assert.Equal(100, maximumPoolSizeOfTheConnectionString);
        Assert.Equal(maximumPoolSizeOfTheConnectionString, maximumPoolSizeOfAWorkerConnection);
    }

    private static async Task<int> ReadMaximumPoolSizeOfAWorkerConnectionAsync(FlowaTestDatabase flowaTestDatabase, string? configuredMaximumPoolSize)
    {
        var maximumPoolSizeSettings = configuredMaximumPoolSize is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?> { ["Database:MaximumPoolSize"] = configuredMaximumPoolSize };
        var workerServiceCollection = new ServiceCollection();
        workerServiceCollection.AddDatadogMetricsInfrastructure(
            flowaTestDatabase.FlowaConnectionString, new ConfigurationBuilder().AddInMemoryCollection(maximumPoolSizeSettings).Build());
        await using var workerServices = workerServiceCollection.BuildServiceProvider();

        await using var workerConnection = await workerServices.GetRequiredService<IDatabaseConnectionSource>().OpenDatabaseConnectionAsync();
        return new NpgsqlConnectionStringBuilder(workerConnection.ConnectionString).MaxPoolSize;
    }
}
