using System.Data.Common;

namespace Flowa.OrderAccumulator.Commons.Database;

public interface IDatabaseConnectionSource
{
    Task<DbConnection> OpenDatabaseConnectionAsync(CancellationToken cancellationToken = default);
}
