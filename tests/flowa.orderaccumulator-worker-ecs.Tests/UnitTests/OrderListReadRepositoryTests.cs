using Flowa.Commons.Database;
using Flowa.OrderAccumulator.Infrastructure.Orders.Repositories;

namespace Flowa.OrderAccumulator.Tests;

// P-01-07: the total and the page of the order list come from the same snapshot, so an order stored between the two
// queries can never show up in one and not in the other.
public sealed class OrderListReadRepositoryTests
{
    [Fact]
    public async Task Total_and_page_of_the_order_list_are_read_inside_one_repeatable_read_snapshot()
    {
        var snapshotRecordingDatabase = new SnapshotRecordingDatabase();

        var storedOrderPage = await new OrderListReadRepository(snapshotRecordingDatabase).ReadStoredOrderPageAsync(3);

        Assert.Equal(1, snapshotRecordingDatabase.SnapshotsOpened);
        Assert.Equal(
            ["inside snapshot: SELECT count(*) FROM orders", "inside snapshot: page of 10 skipping 20"],
            snapshotRecordingDatabase.QueriesRun);
        Assert.Equal((0L, 0), (storedOrderPage.TotalStoredOrders, storedOrderPage.StoredOrders.Count));
    }

    private sealed class SnapshotRecordingDatabase : IDatabase
    {
        private bool isInsideSnapshot;

        public int SnapshotsOpened { get; private set; }

        public List<string> QueriesRun { get; } = [];

        public async Task<TSnapshotRead> ReadInRepeatableReadSnapshotAsync<TSnapshotRead>(Func<Task<TSnapshotRead>> readsInSnapshot, CancellationToken cancellationToken = default)
        {
            SnapshotsOpened++;
            isInsideSnapshot = true;
            try
            {
                return await readsInSnapshot();
            }
            finally
            {
                isInsideSnapshot = false;
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

        private string DescribeSnapshot() => isInsideSnapshot ? "inside snapshot" : "outside snapshot";
    }
}
