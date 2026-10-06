using System.Data.Common;

namespace Flowa.Commons.Database;

public interface IDatabaseConnectionSource
{
    Task<DbConnection> OpenDatabaseConnectionAsync(CancellationToken cancellationToken = default);
}
