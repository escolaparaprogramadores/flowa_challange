using System.Data.Common;

namespace Base.OrderAccumulator.Commons.Database;

public interface IDatabaseConnectionSource
{
    Task<DbConnection> OpenDatabaseConnectionAsync(CancellationToken cancellationToken = default);
}
