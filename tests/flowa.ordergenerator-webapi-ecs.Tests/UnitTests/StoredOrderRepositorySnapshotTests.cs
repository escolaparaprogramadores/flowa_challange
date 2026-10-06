using Flowa.Commons.Database;
using Flowa.OrderGenerator.Infrastructure.Orders.Repositories;

namespace Flowa.OrderGenerator.Tests;

// RF-06 (O-12): the total and the page of the order list come from the same REPEATABLE READ snapshot, so an order
// stored between the two queries can never show up in one and not in the other.
public sealed class StoredOrderRepositorySnapshotTests
{
    [Fact]
    public async Task Total_and_page_of_the_order_list_are_read_inside_one_repeatable_read_snapshot()
    {
        var snapshotRecordingDatabase = new SnapshotRecordingDatabase();

        var storedOrderPage = await new StoredOrderRepository(snapshotRecordingDatabase).ReadStoredOrderPageAsync(3, CancellationToken.None);

        Assert.Equal(1, snapshotRecordingDatabase.SnapshotsOpened);
        Assert.Equal(
            ["inside snapshot: SELECT count(*) FROM orders", "inside snapshot: page of 10 skipping 20"],
            snapshotRecordingDatabase.QueriesRun);
        Assert.Equal((0L, 0), (storedOrderPage.TotalStoredOrders, storedOrderPage.StoredOrders.Count));
    }

    private sealed class SnapshotRecordingDatabase : IDatabase
    {
        private bool _isInsideSnapshot;

        public int SnapshotsOpened { get; private set; }

        public List<string> QueriesRun { get; } = [];

        public async Task<TSnapshotRead> ReadInRepeatableReadSnapshotAsync<TSnapshotRead>(Func<Task<TSnapshotRead>> readsInSnapshot, CancellationToken cancellationToken = default)
        {
            SnapshotsOpened++;
            _isInsideSnapshot = true;
            try
            {
                return await readsInSnapshot();
            }
            finally
            {
                _isInsideSnapshot = false;
            }
        }

        public Task<TScalar?> QueryScalarAsync<TScalar>(string sqlCommand, object? sqlParameters, CancellationToken cancellationToken = default)
        {
            QueriesRun.Add($"{DescribeSnapshot()}: {sqlCommand}");
            return Task.FromResult<TScalar?>(default);
        }

        public Task<IReadOnlyList<TRecord>> QueryRecordsAsync<TRecord>(string sqlCommand, object? sqlParameters, CancellationToken cancellationToken = default)
        {
            var pageParameters = sqlParameters!.GetType();
            QueriesRun.Add(
                $"{DescribeSnapshot()}: page of {pageParameters.GetProperty("OrdersPerPage")!.GetValue(sqlParameters)} skipping {pageParameters.GetProperty("SkippedOrders")!.GetValue(sqlParameters)}");
            return Task.FromResult<IReadOnlyList<TRecord>>([]);
        }

        public Task<TRecord?> QuerySingleRecordAsync<TRecord>(string sqlCommand, object? sqlParameters, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The order list never reads a single record.");

        public Task<int> ExecuteSqlCommandAsync(string sqlCommand, object? sqlParameters, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The order list never writes.");

        private string DescribeSnapshot() => _isInsideSnapshot ? "inside snapshot" : "outside snapshot";
    }
}
