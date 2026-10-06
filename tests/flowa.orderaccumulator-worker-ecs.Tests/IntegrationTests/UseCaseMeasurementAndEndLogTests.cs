using System.Data;
using Flowa.OrderAccumulator.Application.Exposures.Interfaces;
using Flowa.OrderAccumulator.Application.Exposures.UseCases;
using Flowa.OrderAccumulator.Application.Orders.Interfaces;
using Flowa.OrderAccumulator.Application.Orders.Responses;
using Flowa.OrderAccumulator.Application.Orders.UseCases;
using Flowa.Commons.Database;
using Flowa.OrderAccumulator.Domain.Exposures.Interfaces;
using Flowa.OrderAccumulator.Domain.Exposures.ValueObjects;
using Flowa.OrderAccumulator.Domain.Orders.Entities;
using Flowa.OrderAccumulator.Domain.Orders.Interfaces;

namespace Flowa.OrderAccumulator.Tests;

// CA-21 and CA-22 for each use case: the stable operation name, the closed result of success and of failure, and the one
// Information line with the business fact only when the use case ends well (a rejection or a repeat writes none here, G-6).
[Collection(OrderAccumulatorPostgresCollection.Name)]
public sealed class UseCaseMeasurementAndEndLogTests(OrderAccumulatorPostgresFixture orderAccumulatorDatabase) : IAsyncLifetime
{
    private static readonly InvalidOperationException UseCaseFailure = new("database down");

    public Task InitializeAsync() => orderAccumulatorDatabase.ResetOrdersAndExposuresAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Order_decision_records_each_result_and_logs_only_the_new_accepted_order()
    {
        var recordingMonitoring = new RecordingOperationMonitoring();
        var recordingLogger = new RecordingApplicationLogger<DecideIncomingOrderUseCase>();
        var orderDecisionRunner = new DecideIncomingOrderTestRunner(
            orderAccumulatorDatabase.OrderDatabaseConnectionSource, new UncountedOrderMetrics(),
            operationMonitoring: recordingMonitoring, orderDecisionLogger: recordingLogger);
        var failingOrderDecisionRunner = new DecideIncomingOrderTestRunner(
            orderAccumulatorDatabase.OrderDatabaseConnectionSource, new UncountedOrderMetrics(),
            wrapOrderRepository: _ => new OrderRepositoryAnswering(UseCaseFailure), operationMonitoring: recordingMonitoring, orderDecisionLogger: recordingLogger);
        var acceptedSale = TestOrders.NewSellOrder("VIIA4", 99999, 999.99m);

        await orderDecisionRunner.DecideIncomingOrderMessageAsync(acceptedSale);
        await orderDecisionRunner.DecideIncomingOrderMessageAsync(acceptedSale);
        await orderDecisionRunner.DecideIncomingOrderMessageAsync(TestOrders.NewSellOrder("VIIA4", 99999, 999.99m));
        await orderDecisionRunner.DecideIncomingOrderMessageAsync(TestOrders.NewBuyOrder("ITUB4", 100, 10.00m));
        var failedOrderDecision = await failingOrderDecisionRunner.DecideIncomingOrderMessageAsync(TestOrders.NewBuyOrder("PETR4", 1, 1m));

        Assert.Same(UseCaseFailure, failedOrderDecision.Failure);
        Assert.Equal(
            [
                ("orders.decide-incoming-order", "accepted"),
                ("orders.decide-incoming-order", "repeated"),
                ("orders.decide-incoming-order", "rejected"),
                ("orders.decide-incoming-order", "rejected"),
                ("orders.decide-incoming-order", "failed")
            ],
            recordingMonitoring.RecordedOperations);
        Assert.Equal(["Information Order accepted."], recordingLogger.RecordedLogLines);
    }

    [Fact]
    public async Task Order_list_records_success_and_failure_and_logs_only_the_page_read()
    {
        var recordingMonitoring = new RecordingOperationMonitoring();
        var recordingLogger = new RecordingApplicationLogger<ListOrdersUseCase>();

        await new ListOrdersUseCase(new OrderListReadRepositoryAnswering(null), recordingMonitoring, recordingLogger).ListOrdersAsync(1);
        var failedOrderList = await new ListOrdersUseCase(new OrderListReadRepositoryAnswering(UseCaseFailure), recordingMonitoring, recordingLogger).ListOrdersAsync(1);

        Assert.Same(UseCaseFailure, failedOrderList.Failure);
        Assert.Equal([("orders.list-orders", "succeeded"), ("orders.list-orders", "failed")], recordingMonitoring.RecordedOperations);
        Assert.Equal(["Information Stored orders page read."], recordingLogger.RecordedLogLines);
    }

    [Fact]
    public async Task Exposures_read_records_success_and_failure_and_logs_only_the_exposures_read()
    {
        var recordingMonitoring = new RecordingOperationMonitoring();
        var recordingLogger = new RecordingApplicationLogger<GetExposuresUseCase>();

        await new GetExposuresUseCase(new SymbolExposureReadRepositoryAnswering(null), recordingMonitoring, recordingLogger).GetExposuresAsync();
        var failedExposuresRead = await new GetExposuresUseCase(new SymbolExposureReadRepositoryAnswering(UseCaseFailure), recordingMonitoring, recordingLogger).GetExposuresAsync();

        Assert.Same(UseCaseFailure, failedExposuresRead.Failure);
        Assert.Equal([("exposures.get-exposures", "succeeded"), ("exposures.get-exposures", "failed")], recordingMonitoring.RecordedOperations);
        Assert.Equal(["Information Symbol exposures read."], recordingLogger.RecordedLogLines);
    }

    [Fact]
    public async Task Delete_all_records_success_and_failure_and_logs_only_the_deletion()
    {
        var recordingMonitoring = new RecordingOperationMonitoring();
        var recordingLogger = new RecordingApplicationLogger<DeleteAllOrdersUseCase>();

        await new DeleteAllOrdersUseCase(
                new UnitOfWorkWithoutDatabase(), new OrderRepositoryAnswering(null), new ExposureRepositoryAnswering(null),
                recordingMonitoring, recordingLogger)
            .DeleteAllOrdersAsync();
        var failedDeleteAll = await new DeleteAllOrdersUseCase(
                new UnitOfWorkWithoutDatabase(), new OrderRepositoryAnswering(null), new ExposureRepositoryAnswering(UseCaseFailure),
                recordingMonitoring, recordingLogger)
            .DeleteAllOrdersAsync();

        Assert.Same(UseCaseFailure, failedDeleteAll.Failure);
        Assert.Equal([("orders.delete-all-orders", "succeeded"), ("orders.delete-all-orders", "failed")], recordingMonitoring.RecordedOperations);
        Assert.Equal(["Information All orders deleted and symbol exposures zeroed."], recordingLogger.RecordedLogLines);
    }

    private sealed class OrderListReadRepositoryAnswering(Exception? readFailure) : IOrderListReadRepository
    {
        public Task<StoredOrderPageResponse> ReadStoredOrderPageAsync(int pageNumber, CancellationToken cancellationToken = default) =>
            readFailure is null ? Task.FromResult(new StoredOrderPageResponse(0, [])) : Task.FromException<StoredOrderPageResponse>(readFailure);
    }

    private sealed class SymbolExposureReadRepositoryAnswering(Exception? readFailure) : ISymbolExposureReadRepository
    {
        public Task<IReadOnlyList<SymbolExposure>> GetSymbolExposuresAsync(CancellationToken cancellationToken = default) =>
            readFailure is null
                ? Task.FromResult<IReadOnlyList<SymbolExposure>>([new("PETR4", 0m), new("VALE3", 0m), new("VIIA4", 0m)])
                : Task.FromException<IReadOnlyList<SymbolExposure>>(readFailure);
    }

    private sealed class OrderRepositoryAnswering(Exception? orderFailure) : IOrderRepository
    {
        public Task<Order?> FindOrderByClOrdIdAsync(string clOrdId, CancellationToken cancellationToken = default) =>
            orderFailure is null ? Task.FromResult<Order?>(null) : Task.FromException<Order?>(orderFailure);

        public Task<bool> TryAddOrderAsync(Order answeredOrder, CancellationToken cancellationToken = default) =>
            orderFailure is null ? Task.FromResult(true) : Task.FromException<bool>(orderFailure);

        public Task DeleteAllOrdersAsync(CancellationToken cancellationToken = default) =>
            orderFailure is null ? Task.CompletedTask : Task.FromException(orderFailure);
    }

    private sealed class ExposureRepositoryAnswering(Exception? zeroingFailure) : IExposureRepository
    {
        public Task<bool> TryMoveSymbolExposureWithinLimitAsync(
            string orderSymbol, decimal exposureDelta, decimal exposureLimit, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task ZeroSymbolExposuresAsync(IReadOnlyList<string> orderSymbols, CancellationToken cancellationToken = default) =>
            zeroingFailure is null ? Task.CompletedTask : Task.FromException(zeroingFailure);
    }

    private sealed class UnitOfWorkWithoutDatabase : IUnitOfWork
    {
        public Task BeginTransactionAsync(IsolationLevel transactionIsolationLevel, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task CommitTransactionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RollbackTransactionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
