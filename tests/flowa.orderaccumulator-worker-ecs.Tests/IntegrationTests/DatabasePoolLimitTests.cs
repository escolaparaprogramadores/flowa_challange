using Flowa.Commons.Database;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Flowa.OrderAccumulator.Tests;

// CA-32: the Database:MaximumPoolSize the app reads is the limit of the connections it really opens; without the key
// the connection keeps the limit written in the connection string (250 in the test database).
[Collection(OrderAccumulatorPostgresCollection.Name)]
public sealed class DatabasePoolLimitTests(OrderAccumulatorPostgresFixture orderAccumulatorDatabase)
{
    [Fact]
    public async Task Connections_the_order_accumulator_opens_carry_the_maximum_pool_size_of_the_key()
    {
        Assert.Equal(7, await ReadMaximumPoolSizeOfAnAppConnectionAsync("7"));
    }

    [Fact]
    public async Task Without_the_key_the_connections_keep_the_limit_of_the_connection_string()
    {
        Assert.Equal(250, await ReadMaximumPoolSizeOfAnAppConnectionAsync(configuredMaximumPoolSize: null));
    }

    private async Task<int> ReadMaximumPoolSizeOfAnAppConnectionAsync(string? configuredMaximumPoolSize)
    {
        var maximumPoolSizeSettings = configuredMaximumPoolSize is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?> { ["Database:MaximumPoolSize"] = configuredMaximumPoolSize };
        await using var orderAccumulatorTestApp = await new OrderAccumulatorFixTestHost(
            orderAccumulatorDatabase.OrderDatabaseConnectionString, extraOrderAccumulatorSettings: maximumPoolSizeSettings).StartWithFixAcceptorAsync();

        await using var appConnection = await orderAccumulatorTestApp.Services.GetRequiredService<IDatabaseConnectionSource>().OpenDatabaseConnectionAsync();
        return new NpgsqlConnectionStringBuilder(appConnection.ConnectionString).MaxPoolSize;
    }
}
