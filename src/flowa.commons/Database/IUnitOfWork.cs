using System.Data;

namespace Flowa.Commons.Database;

public interface IUnitOfWork
{
    Task BeginTransactionAsync(IsolationLevel transactionIsolationLevel, CancellationToken cancellationToken = default);

    Task CommitTransactionAsync(CancellationToken cancellationToken = default);

    Task RollbackTransactionAsync(CancellationToken cancellationToken = default);
}
