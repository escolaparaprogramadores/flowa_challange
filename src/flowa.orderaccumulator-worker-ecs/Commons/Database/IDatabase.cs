namespace Flowa.OrderAccumulator.Commons.Database;

public interface IDatabase
{
    Task<TRecord?> QuerySingleRecordAsync<TRecord>(string sqlCommand, object? sqlParameters, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TRecord>> QueryRecordsAsync<TRecord>(string sqlCommand, object? sqlParameters, CancellationToken cancellationToken = default);

    Task<TScalar?> QueryScalarAsync<TScalar>(string sqlCommand, object? sqlParameters, CancellationToken cancellationToken = default);

    Task<int> ExecuteSqlCommandAsync(string sqlCommand, object? sqlParameters, CancellationToken cancellationToken = default);

    Task<TSnapshotRead> ReadInRepeatableReadSnapshotAsync<TSnapshotRead>(Func<Task<TSnapshotRead>> readsInSnapshot, CancellationToken cancellationToken = default);
}
