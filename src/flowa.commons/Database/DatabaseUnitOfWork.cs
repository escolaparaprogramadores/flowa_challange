using System.Data.Common;
using System.Data;

namespace Flowa.Commons.Database;

public sealed class DatabaseUnitOfWork : IUnitOfWork, IAsyncDisposable
{
    private readonly IDatabaseConnectionSource databaseConnectionSource;
    private DbConnection? openDatabaseConnection;

    public DatabaseUnitOfWork(IDatabaseConnectionSource databaseConnectionSource)
    {
        this.databaseConnectionSource = databaseConnectionSource ?? throw new ArgumentNullException(nameof(databaseConnectionSource));
    }

    public DbTransaction? CurrentTransaction { get; private set; }

    public async Task<DbConnection> GetOpenDatabaseConnectionAsync(CancellationToken cancellationToken = default) =>
        openDatabaseConnection ??= await databaseConnectionSource.OpenDatabaseConnectionAsync(cancellationToken);

    public async Task BeginTransactionAsync(IsolationLevel transactionIsolationLevel, CancellationToken cancellationToken = default)
    {
        var databaseConnection = await GetOpenDatabaseConnectionAsync(cancellationToken);
        CurrentTransaction = await databaseConnection.BeginTransactionAsync(transactionIsolationLevel, cancellationToken);
    }

    public async Task CommitTransactionAsync(CancellationToken cancellationToken = default)
    {
        var openTransaction = CurrentTransaction ?? throw new InvalidOperationException("There is no open transaction to commit.");
        await openTransaction.CommitAsync(cancellationToken);
        await EndTransactionAsync();
    }

    public async Task RollbackTransactionAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentTransaction is null)
            return;

        if (CurrentTransaction.Connection is not null)
            await CurrentTransaction.RollbackAsync(cancellationToken);
        await EndTransactionAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await EndTransactionAsync();
        if (openDatabaseConnection is not null)
            await openDatabaseConnection.DisposeAsync();
    }

    private async Task EndTransactionAsync()
    {
        if (CurrentTransaction is not null)
            await CurrentTransaction.DisposeAsync();
        CurrentTransaction = null;
    }
}
