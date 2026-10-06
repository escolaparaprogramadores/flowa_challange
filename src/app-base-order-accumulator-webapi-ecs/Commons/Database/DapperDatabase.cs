using System.Data;
using Dapper;

namespace Base.OrderAccumulator.Commons.Database;

public sealed class DapperDatabase : IDatabase
{
    private readonly DatabaseUnitOfWork databaseUnitOfWork;

    public DapperDatabase(DatabaseUnitOfWork databaseUnitOfWork)
    {
        this.databaseUnitOfWork = databaseUnitOfWork ?? throw new ArgumentNullException(nameof(databaseUnitOfWork));
    }

    public async Task<TRecord?> QuerySingleRecordAsync<TRecord>(string sqlCommand, object? sqlParameters, CancellationToken cancellationToken = default)
    {
        var databaseConnection = await databaseUnitOfWork.GetOpenDatabaseConnectionAsync(cancellationToken);
        return await databaseConnection.QuerySingleOrDefaultAsync<TRecord>(BuildDapperCommand(sqlCommand, sqlParameters, cancellationToken));
    }

    public async Task<IReadOnlyList<TRecord>> QueryRecordsAsync<TRecord>(string sqlCommand, object? sqlParameters, CancellationToken cancellationToken = default)
    {
        var databaseConnection = await databaseUnitOfWork.GetOpenDatabaseConnectionAsync(cancellationToken);
        var foundRecords = await databaseConnection.QueryAsync<TRecord>(BuildDapperCommand(sqlCommand, sqlParameters, cancellationToken));
        return foundRecords.AsList();
    }

    public async Task<TScalar?> QueryScalarAsync<TScalar>(string sqlCommand, object? sqlParameters, CancellationToken cancellationToken = default)
    {
        var databaseConnection = await databaseUnitOfWork.GetOpenDatabaseConnectionAsync(cancellationToken);
        return await databaseConnection.ExecuteScalarAsync<TScalar>(BuildDapperCommand(sqlCommand, sqlParameters, cancellationToken));
    }

    public async Task<int> ExecuteSqlCommandAsync(string sqlCommand, object? sqlParameters, CancellationToken cancellationToken = default)
    {
        var databaseConnection = await databaseUnitOfWork.GetOpenDatabaseConnectionAsync(cancellationToken);
        return await databaseConnection.ExecuteAsync(BuildDapperCommand(sqlCommand, sqlParameters, cancellationToken));
    }

    public async Task<TSnapshotRead> ReadInRepeatableReadSnapshotAsync<TSnapshotRead>(Func<Task<TSnapshotRead>> readsInSnapshot, CancellationToken cancellationToken = default)
    {
        await databaseUnitOfWork.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        try
        {
            var snapshotRead = await readsInSnapshot();
            await databaseUnitOfWork.CommitTransactionAsync(cancellationToken);
            return snapshotRead;
        }
        finally
        {
            await databaseUnitOfWork.RollbackTransactionAsync(CancellationToken.None);
        }
    }

    private CommandDefinition BuildDapperCommand(string sqlCommand, object? sqlParameters, CancellationToken cancellationToken) =>
        new(sqlCommand, sqlParameters, databaseUnitOfWork.CurrentTransaction, cancellationToken: cancellationToken);
}
