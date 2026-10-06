using Base.OrderAccumulator.Commons;
using Npgsql;

namespace Base.OrderAccumulator.Infrastructure.Persistence;

// One connection per operation (scoped). The repositories of the same operation share it, and
// share the transaction while one is open.
public sealed class PostgresUnitOfWork(NpgsqlDataSource orderDatabaseDataSource) : IUnitOfWork, IAsyncDisposable
{
    private NpgsqlConnection? openOrderDatabaseConnection;

    public NpgsqlTransaction? CurrentTransaction { get; private set; }

    public async Task<NpgsqlConnection> GetOpenConnectionAsync(CancellationToken cancellationToken = default) =>
        openOrderDatabaseConnection ??= await orderDatabaseDataSource.OpenConnectionAsync(cancellationToken);

    public async Task BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        var orderDatabaseConnection = await GetOpenConnectionAsync(cancellationToken);
        CurrentTransaction = await orderDatabaseConnection.BeginTransactionAsync(cancellationToken);
    }

    public async Task CommitTransactionAsync(CancellationToken cancellationToken = default)
    {
        var openTransaction = CurrentTransaction ?? throw new InvalidOperationException("There is no open transaction to commit.");
        await openTransaction.CommitAsync(cancellationToken);
        await EndTransactionAsync();
    }

    // Without an open transaction there is nothing to undo: the use case may call this again from its
    // catch after a rollback or a failed commit.
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
        if (openOrderDatabaseConnection is not null)
            await openOrderDatabaseConnection.DisposeAsync();
    }

    private async Task EndTransactionAsync()
    {
        if (CurrentTransaction is not null)
            await CurrentTransaction.DisposeAsync();
        CurrentTransaction = null;
    }
}
