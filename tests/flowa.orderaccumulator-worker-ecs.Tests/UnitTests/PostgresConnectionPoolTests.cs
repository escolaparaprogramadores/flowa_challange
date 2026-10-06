using Flowa.Commons.Database;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace Flowa.OrderAccumulator.Tests;

// CA-32: the pool limit comes from Database:MaximumPoolSize, outside the shared secret, and the rest of the
// connection string stays exactly as the secret wrote it. The test credential is generated at run time.
public sealed class PostgresConnectionPoolTests
{
    private static readonly string GeneratedTestCredential = Guid.NewGuid().ToString("N");

    private static readonly string SecretConnectionString = new NpgsqlConnectionStringBuilder
    {
        Host = "flowa-db.example",
        Port = 5432,
        Database = "flowa",
        Username = "flowa_app",
        SslMode = SslMode.Require,
        ["Password"] = GeneratedTestCredential
    }.ConnectionString;

    [Fact]
    public void Maximum_pool_size_from_the_key_is_applied_and_every_other_item_of_the_secret_stays_the_same()
    {
        var pooledConnectionString = PostgresConnectionPool.ApplyMaximumPoolSize(SecretConnectionString, "7");

        var pooledConnection = new NpgsqlConnectionStringBuilder(pooledConnectionString);
        var secretConnection = new NpgsqlConnectionStringBuilder(SecretConnectionString);
        Assert.Equal(7, pooledConnection.MaxPoolSize);
        Assert.Equal(
            (secretConnection.Host, secretConnection.Port, secretConnection.Database, secretConnection.Username, secretConnection.Password, secretConnection.SslMode),
            (pooledConnection.Host, pooledConnection.Port, pooledConnection.Database, pooledConnection.Username, pooledConnection.Password, pooledConnection.SslMode));
        Assert.Equal(GeneratedTestCredential, pooledConnection.Password);
        secretConnection.MaxPoolSize = 7;
        Assert.True(secretConnection.EquivalentTo(pooledConnection));
    }

    [Fact]
    public void Without_the_key_the_connection_string_is_the_secret_untouched()
    {
        Assert.Equal(SecretConnectionString, PostgresConnectionPool.ApplyMaximumPoolSize(SecretConnectionString, null));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("ten")]
    [InlineData("2.5")]
    [InlineData("")]
    public void Maximum_pool_size_that_is_not_a_whole_number_above_zero_is_refused_naming_the_key(string configuredMaximumPoolSize)
    {
        var refusedPoolSize = Assert.Throws<InvalidOperationException>(
            () => PostgresConnectionPool.ApplyMaximumPoolSize(SecretConnectionString, configuredMaximumPoolSize));

        Assert.Equal("Set Database:MaximumPoolSize to a whole number greater than zero.", refusedPoolSize.Message);
        Assert.DoesNotContain(GeneratedTestCredential, refusedPoolSize.Message);
    }

    [Fact]
    public void Order_accumulator_reads_the_key_at_startup_and_does_not_start_with_an_invalid_pool_size()
    {
        using var orderAccumulatorWithInvalidPool = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(orderAccumulatorHost => orderAccumulatorHost
                .UseSetting("ConnectionStrings:Flowa", SecretConnectionString)
                .UseSetting("Database:MaximumPoolSize", "zero"));

        var startupFailure = Assert.Throws<InvalidOperationException>(() => orderAccumulatorWithInvalidPool.CreateClient());

        Assert.Equal("Set Database:MaximumPoolSize to a whole number greater than zero.", startupFailure.Message);
    }
}
