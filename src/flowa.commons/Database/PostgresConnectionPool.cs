using System.Globalization;
using Npgsql;

namespace Flowa.Commons.Database;

public static class PostgresConnectionPool
{
    public const string MaximumPoolSizeKey = "Database:MaximumPoolSize";

    public static string ApplyMaximumPoolSize(string databaseConnectionString, string? configuredMaximumPoolSize)
    {
        if (configuredMaximumPoolSize is null)
            return databaseConnectionString;

        if (!int.TryParse(configuredMaximumPoolSize, NumberStyles.None, CultureInfo.InvariantCulture, out var maximumPoolSize) || maximumPoolSize < 1)
            throw new InvalidOperationException($"Set {MaximumPoolSizeKey} to a whole number greater than zero.");

        return new NpgsqlConnectionStringBuilder(databaseConnectionString) { MaxPoolSize = maximumPoolSize }.ConnectionString;
    }
}
