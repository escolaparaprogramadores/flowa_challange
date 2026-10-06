using System.Data.Common;
using Npgsql;

namespace Base.OrderAccumulator.Commons.Database;

public sealed class PostgresConnectionSource : IDatabaseConnectionSource, IAsyncDisposable, IDisposable
{
    private readonly NpgsqlDataSource postgresDataSource;

    public PostgresConnectionSource(string databaseConnectionString)
    {
        postgresDataSource = NpgsqlDataSource.Create(databaseConnectionString);
    }

    public async Task<DbConnection> OpenDatabaseConnectionAsync(CancellationToken cancellationToken = default) =>
        await postgresDataSource.OpenConnectionAsync(cancellationToken);

    public ValueTask DisposeAsync() => postgresDataSource.DisposeAsync();

    public void Dispose() => postgresDataSource.Dispose();
}
