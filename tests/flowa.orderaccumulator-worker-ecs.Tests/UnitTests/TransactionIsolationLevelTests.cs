using System.Data;
using Flowa.Commons.Database;
using Flowa.OrderAccumulator.Application.Orders.UseCases;
using Flowa.OrderAccumulator.Domain.DomainServices;
using Flowa.OrderAccumulator.Domain.Exposures.Interfaces;
using Flowa.OrderAccumulator.Domain.Orders.Entities;
using Flowa.OrderAccumulator.Domain.Orders.Interfaces;

namespace Flowa.OrderAccumulator.Tests;

// O-12: the order decision and the delete open their transaction in READ COMMITTED, so a delete running in another
// process makes them wait for the exposure rows instead of failing with a serialization error.
public sealed class TransactionIsolationLevelTests
{
    [Fact]
    public async Task Order_decision_opens_its_transaction_in_read_committed()
    {
        var recordingUnitOfWork = new UnitOfWorkRecordingIsolationLevels();
        var orderDecisionUseCase = new DecideIncomingOrderUseCase(
            recordingUnitOfWork, new OrderRepositoryStoringEveryOrder(), new OrderDecisionDomainService(new ExposureAlwaysWithinLimit()),
            new UncountedOrderMetrics(), TestObservability.CreateOperationMonitoring(), TestObservability.CreateDiscardingLogger<DecideIncomingOrderUseCase>());

        var orderDecisionMessage = await orderDecisionUseCase.DecideIncomingOrderAsync(TestOrders.NewBuyOrder("PETR4", 100, 10.00m));

        Assert.True(orderDecisionMessage.Success);
        Assert.Equal([IsolationLevel.ReadCommitted], recordingUnitOfWork.OpenedIsolationLevels);
    }

    [Fact]
    public async Task Delete_of_all_orders_opens_its_transaction_in_read_committed()
    {
        var recordingUnitOfWork = new UnitOfWorkRecordingIsolationLevels();
        var deleteAllOrdersUseCase = new DeleteAllOrdersUseCase(
            recordingUnitOfWork, new OrderRepositoryStoringEveryOrder(), new ExposureAlwaysWithinLimit(),
            TestObservability.CreateOperationMonitoring(), TestObservability.CreateDiscardingLogger<DeleteAllOrdersUseCase>());

        var deleteAllOrdersMessage = await deleteAllOrdersUseCase.DeleteAllOrdersAsync();

        Assert.True(deleteAllOrdersMessage.Success);
        Assert.Equal([IsolationLevel.ReadCommitted], recordingUnitOfWork.OpenedIsolationLevels);
    }

    private sealed class UnitOfWorkRecordingIsolationLevels : IUnitOfWork
    {
        private readonly List<IsolationLevel> openedIsolationLevels = [];

        public IReadOnlyList<IsolationLevel> OpenedIsolationLevels => openedIsolationLevels;

        public Task BeginTransactionAsync(IsolationLevel transactionIsolationLevel, CancellationToken cancellationToken = default)
        {
            openedIsolationLevels.Add(transactionIsolationLevel);
            return Task.CompletedTask;
        }

        public Task CommitTransactionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RollbackTransactionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class OrderRepositoryStoringEveryOrder : IOrderRepository
    {
        public Task<Order?> FindOrderByClOrdIdAsync(string clOrdId, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(null);

        public Task<bool> TryAddOrderAsync(Order answeredOrder, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task DeleteAllOrdersAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ExposureAlwaysWithinLimit : IExposureRepository
    {
        public Task<bool> TryMoveSymbolExposureWithinLimitAsync(
            string orderSymbol, decimal exposureDelta, decimal exposureLimit, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task ZeroSymbolExposuresAsync(IReadOnlyList<string> orderSymbols, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
