using System.Data;
using Flowa.OrderAccumulator.Application.Exposures.Interfaces;
using Flowa.OrderAccumulator.Application.Orders.Responses;
using Flowa.OrderAccumulator.Application.Orders.UseCases;
using Flowa.Commons.Database;
using Flowa.OrderAccumulator.Domain.DomainServices;
using Flowa.OrderAccumulator.Domain.Exposures.Interfaces;
using Flowa.OrderAccumulator.Domain.Exposures.ValueObjects;
using Flowa.OrderAccumulator.Domain.Orders.Entities;
using Flowa.OrderAccumulator.Domain.Orders.Enums;
using Flowa.OrderAccumulator.Domain.Orders.Interfaces;
using Flowa.OrderAccumulator.Domain.Orders.ValueObjects;
using Flowa.OrderAccumulator.Infrastructure.DependencyInjection;
using Flowa.OrderAccumulator.Infrastructure.Exposures.Adapters;
using Flowa.OrderAccumulator.Infrastructure.Orders.Adapters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Flowa.OrderAccumulator.Tests;

// CA-30 and CA-31: an order and "Delete all" never interleave, and in the end database and memory report the same exposure.
[Collection(OrderAccumulatorPostgresCollection.Name)]
public sealed class DeleteAllOrdersConcurrencyTests(OrderAccumulatorPostgresFixture orderAccumulatorDatabase, ITestOutputHelper concurrencyTestOutput)
{
    private const int OrdersPerWave = 100;
    private const int StoredOrdersCountBeforeFiringTheDeletes = 30;
    private const int SimultaneousDeletesPerRound = 5;

    // Time for a step that should not happen to have happened, if the door were open.
    private static readonly TimeSpan TimeForABlockedStepToSneakIn = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ConcurrencyStepDeadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Delete_waits_for_the_order_in_progress_and_zeroes_the_memory_after_it()
    {
        var symbolExposureMemory = new InMemorySymbolExposureAdapter();
        var orderInsideTheDoor = new TaskCompletionSource();
        var releaseTheOrder = new TaskCompletionSource();
        var orderInProgress = symbolExposureMemory.DecideOrderOutsideDeleteAllAsync(async () =>
        {
            orderInsideTheDoor.SetResult();
            await releaseTheOrder.Task;
            var acceptedOrderDecision = CreateAcceptedBuyOrderDecision("PETR4", 100, 10.00m);
            symbolExposureMemory.ApplyAcceptedOrder(acceptedOrderDecision);
            return acceptedOrderDecision;
        }, CancellationToken.None);
        await orderInsideTheDoor.Task.WaitAsync(ConcurrencyStepDeadline);

        var hasStoredDeleteStarted = false;
        var deleteAllOrdersTask = symbolExposureMemory.DeleteAllOrdersAndZeroExposuresAsync(() =>
        {
            hasStoredDeleteStarted = true;
            return Task.CompletedTask;
        }, CancellationToken.None);
        await Task.Delay(TimeForABlockedStepToSneakIn);
        var hadStoredDeleteStartedWhileTheOrderWasInside = hasStoredDeleteStarted;
        releaseTheOrder.SetResult();
        await Task.WhenAll(orderInProgress, deleteAllOrdersTask).WaitAsync(ConcurrencyStepDeadline);

        Assert.False(hadStoredDeleteStartedWhileTheOrderWasInside);
        Assert.True(hasStoredDeleteStarted);
        Assert.Equal([new("PETR4", 0m), new("VALE3", 0m), new("VIIA4", 0m)], symbolExposureMemory.ReadCurrentSymbolExposures());
    }

    [Fact]
    public async Task Order_arriving_during_a_delete_waits_and_adds_up_from_zero_after_it()
    {
        var symbolExposureMemory = new InMemorySymbolExposureAdapter();
        symbolExposureMemory.LoadStoredExposures([new SymbolExposure("PETR4", 5_000m)]);
        var deleteInsideTheDoor = new TaskCompletionSource();
        var releaseTheDelete = new TaskCompletionSource();
        var deleteAllOrdersTask = symbolExposureMemory.DeleteAllOrdersAndZeroExposuresAsync(async () =>
        {
            deleteInsideTheDoor.SetResult();
            await releaseTheDelete.Task;
        }, CancellationToken.None);
        await deleteInsideTheDoor.Task.WaitAsync(ConcurrencyStepDeadline);

        var hasOrderProcessingStarted = false;
        var orderArrivingDuringDelete = symbolExposureMemory.DecideOrderOutsideDeleteAllAsync(() =>
        {
            hasOrderProcessingStarted = true;
            var acceptedOrderDecision = CreateAcceptedBuyOrderDecision("PETR4", 100, 10.00m);
            symbolExposureMemory.ApplyAcceptedOrder(acceptedOrderDecision);
            return Task.FromResult(acceptedOrderDecision);
        }, CancellationToken.None);
        await Task.Delay(TimeForABlockedStepToSneakIn);
        var hadOrderStartedWhileTheDeleteWasInside = hasOrderProcessingStarted;
        releaseTheDelete.SetResult();
        await Task.WhenAll(deleteAllOrdersTask, orderArrivingDuringDelete).WaitAsync(ConcurrencyStepDeadline);

        Assert.False(hadOrderStartedWhileTheDeleteWasInside);
        Assert.Equal([new("PETR4", 1_000.00m), new("VALE3", 0m), new("VIIA4", 0m)], symbolExposureMemory.ReadCurrentSymbolExposures());
    }

    [Fact]
    public async Task Orders_still_run_side_by_side_when_no_delete_is_waiting()
    {
        var symbolExposureMemory = new InMemorySymbolExposureAdapter();
        var firstOrderInside = new TaskCompletionSource();
        var secondOrderInside = new TaskCompletionSource();

        var firstOrderTask = symbolExposureMemory.DecideOrderOutsideDeleteAllAsync(async () =>
        {
            firstOrderInside.SetResult();
            await secondOrderInside.Task;
            return CreateAcceptedBuyOrderDecision("PETR4", 1, 1.00m);
        }, CancellationToken.None);
        var secondOrderTask = symbolExposureMemory.DecideOrderOutsideDeleteAllAsync(async () =>
        {
            secondOrderInside.SetResult();
            await firstOrderInside.Task;
            return CreateAcceptedBuyOrderDecision("VALE3", 1, 1.00m);
        }, CancellationToken.None);

        await Task.WhenAll(firstOrderTask, secondOrderTask).WaitAsync(ConcurrencyStepDeadline);
    }

    // A continuous stream of orders cannot leave the delete waiting forever: a new order that arrives
    // after the delete waits for it to pass.
    [Fact]
    public async Task Order_arriving_after_a_waiting_delete_runs_only_after_the_delete()
    {
        var symbolExposureMemory = new InMemorySymbolExposureAdapter();
        var firstOrderInside = new TaskCompletionSource();
        var releaseTheFirstOrder = new TaskCompletionSource();
        var firstOrderTask = symbolExposureMemory.DecideOrderOutsideDeleteAllAsync(async () =>
        {
            firstOrderInside.SetResult();
            await releaseTheFirstOrder.Task;
            return CreateAcceptedBuyOrderDecision("PETR4", 1, 1.00m);
        }, CancellationToken.None);
        await firstOrderInside.Task.WaitAsync(ConcurrencyStepDeadline);

        var executedStepsInOrder = new List<string>();
        var deleteAllOrdersTask = symbolExposureMemory.DeleteAllOrdersAndZeroExposuresAsync(() =>
        {
            lock (executedStepsInOrder) executedStepsInOrder.Add("delete");
            return Task.CompletedTask;
        }, CancellationToken.None);
        await Task.Delay(TimeForABlockedStepToSneakIn);
        var laterOrderTask = symbolExposureMemory.DecideOrderOutsideDeleteAllAsync(() =>
        {
            lock (executedStepsInOrder) executedStepsInOrder.Add("order that arrived later");
            return Task.FromResult(CreateAcceptedBuyOrderDecision("VALE3", 1, 1.00m));
        }, CancellationToken.None);
        await Task.Delay(TimeForABlockedStepToSneakIn);
        var stepsWhileTheFirstOrderWasInside = executedStepsInOrder.ToList();
        releaseTheFirstOrder.SetResult();
        await Task.WhenAll(firstOrderTask, deleteAllOrdersTask, laterOrderTask).WaitAsync(ConcurrencyStepDeadline);

        Assert.Empty(stepsWhileTheFirstOrderWasInside);
        Assert.Equal(["delete", "order that arrived later"], executedStepsInOrder);
    }

    // Through the real DecideIncomingOrderUseCase: the already stored order only leaves the door after adding to memory.
    // If the addition happened outside the door, the waiting delete would see memory without the order and the zero
    // would be undone by the late addition. Repeats so the window between storing and adding is exercised many times.
    [Fact]
    public async Task Order_through_the_use_case_adds_to_memory_before_a_waiting_delete_runs()
    {
        using var orderMetricsClient = OrderMetricsExtensions.CreateOrderMetricsClient(FindDogStatsdPortWithoutListener(), new ConfigurationBuilder().Build());
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var symbolExposureMemory = new InMemorySymbolExposureAdapter();
            var storedOrderHeldUntilReleased = new StoredOrderHeldUntilReleased();
            var decideIncomingOrderUseCase = new DecideIncomingOrderUseCase(
                new UnitOfWorkWithoutDatabase(), storedOrderHeldUntilReleased, new OrderDecisionDomainService(new SymbolExposureAlwaysWithinLimit()),
                symbolExposureMemory, new DatadogOrderMetricsAdapter(orderMetricsClient),
                TestObservability.CreateOperationMonitoring(), TestObservability.CreateDiscardingLogger<DecideIncomingOrderUseCase>());

            var orderInProgress = decideIncomingOrderUseCase.DecideIncomingOrderAsync(TestOrders.NewBuyOrder("PETR4", 100, 10.00m));
            await storedOrderHeldUntilReleased.OrderStored.WaitAsync(ConcurrencyStepDeadline);
            IReadOnlyList<SymbolExposure>? exposureMemorySeenByTheDelete = null;
            var deleteAllOrdersTask = symbolExposureMemory.DeleteAllOrdersAndZeroExposuresAsync(() =>
            {
                exposureMemorySeenByTheDelete = symbolExposureMemory.ReadCurrentSymbolExposures();
                return Task.CompletedTask;
            }, CancellationToken.None);
            await Task.Delay(20);
            storedOrderHeldUntilReleased.ReleaseTheStoredOrder();
            await Task.WhenAll(orderInProgress, deleteAllOrdersTask).WaitAsync(ConcurrencyStepDeadline);

            Assert.Equal([new("PETR4", 1_000.00m), new("VALE3", 0m), new("VIIA4", 0m)], exposureMemorySeenByTheDelete);
            Assert.Equal([new("PETR4", 0m), new("VALE3", 0m), new("VIIA4", 0m)], symbolExposureMemory.ReadCurrentSymbolExposures());
        }
    }

    // Through the whole app: 100 orders on the three symbols; when 30 are already stored, 5 deletes through the route
    // come in among the ones still in progress; after the deletes, 100 more orders. No exception and no deadlock; in the
    // end orders remain (the ones that came after the last delete) and the database exposure equals the memory one and
    // the sum of the remaining accepted orders.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task Deletes_in_the_middle_of_orders_in_progress_end_with_the_same_exposure_in_the_database_and_in_memory(int concurrencyRound)
    {
        await orderAccumulatorDatabase.ResetOrdersAndExposuresAsync();
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();
        var appOrderDecisionServices = orderAccumulatorTestApp.Services;
        var appSymbolExposureMemory = orderAccumulatorTestApp.Services.GetRequiredService<ISymbolExposureMemoryPort>();
        var orderAccumulatorClient = orderAccumulatorTestApp.CreateClient();
        var orderQuantityGenerator = new Random(concurrencyRound);
        var storedOrdersCountBeforeTheDeletes = 0;
        var enoughOrdersStoredToFireTheDeletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var ordersBeforeTheDeletes = NewOrdersMixingSymbolsAndSides(OrdersPerWave, orderQuantityGenerator).Select(symbolAndSideMixedOrder => Task.Run(async () =>
        {
            var orderDecision = await appOrderDecisionServices.DecideIncomingOrderAsync(symbolAndSideMixedOrder);
            if (Interlocked.Increment(ref storedOrdersCountBeforeTheDeletes) == StoredOrdersCountBeforeFiringTheDeletes)
                enoughOrdersStoredToFireTheDeletes.TrySetResult();
            return orderDecision;
        })).ToList();
        await enoughOrdersStoredToFireTheDeletes.Task.WaitAsync(TimeSpan.FromMinutes(2));
        // Counts by the orders already stored, not by Task.IsCompleted: the task that fires the signal has not finished
        // yet when the test wakes up and would count as "in progress", adding one too many.
        var ordersInProgressCountWhenTheDeletesFired = OrdersPerWave - Volatile.Read(ref storedOrdersCountBeforeTheDeletes);
        var deleteResponses = await Task.WhenAll(Enumerable.Range(0, SimultaneousDeletesPerRound)
            .Select(_ => Task.Run(() => orderAccumulatorClient.DeleteAsync("/api/orders")))).WaitAsync(TimeSpan.FromMinutes(2));
        var ordersAfterTheDeletes = NewOrdersMixingSymbolsAndSides(OrdersPerWave, orderQuantityGenerator)
            .Select(symbolAndSideMixedOrder => Task.Run(() => appOrderDecisionServices.DecideIncomingOrderAsync(symbolAndSideMixedOrder))).ToList();
        var orderDecisions = await Task.WhenAll(ordersBeforeTheDeletes.Concat(ordersAfterTheDeletes)).WaitAsync(TimeSpan.FromMinutes(2));

        var remainingStoredOrdersCount = await orderAccumulatorDatabase.CountStoredOrdersAsync();
        var storedExposures = await orderAccumulatorDatabase.ExposureReader.GetSymbolExposuresAsync();
        concurrencyTestOutput.WriteLine(
            $"round {concurrencyRound}: {ordersInProgressCountWhenTheDeletesFired} orders in progress when the deletes fired; " +
            $"{remainingStoredOrdersCount} orders remained; " +
            string.Join(", ", storedExposures.Select(storedExposure => $"{storedExposure.Symbol}={storedExposure.Exposure}")));
        Assert.All(orderDecisions, orderDecision => Assert.True(orderDecision.Accepted));
        Assert.All(deleteResponses, deleteResponse => Assert.Equal(System.Net.HttpStatusCode.NoContent, deleteResponse.StatusCode));
        Assert.InRange(ordersInProgressCountWhenTheDeletesFired, 1, OrdersPerWave - StoredOrdersCountBeforeFiringTheDeletes);
        Assert.InRange(remainingStoredOrdersCount, OrdersPerWave, 2 * OrdersPerWave - StoredOrdersCountBeforeFiringTheDeletes);
        Assert.Equal(storedExposures, appSymbolExposureMemory.ReadCurrentSymbolExposures());
        foreach (var storedExposure in storedExposures)
            Assert.Equal(await orderAccumulatorDatabase.SumAcceptedOrdersExposureAsync(storedExposure.Symbol), storedExposure.Exposure);
    }

    private static List<IncomingOrder> NewOrdersMixingSymbolsAndSides(int orderCount, Random orderQuantityGenerator) =>
        Enumerable.Range(0, orderCount)
            .Select(orderNumber => TestOrders.NewIncomingOrder(
                OrderFieldPolicy.AllowedOrderSymbols[orderNumber % 3],
                orderNumber % 2 == 0 ? OrderSideCodes.BuyOrderSideFixCode : OrderSideCodes.SellOrderSideFixCode,
                orderQuantityGenerator.Next(1, 1_000), 10.00m))
            .ToList();

    private static int FindDogStatsdPortWithoutListener() => OrderAccumulatorFixTestHost.FindFreeFixAcceptorTcpPort();

    // Plays the order repository: the order "was stored" and waits there until the test lets it go.
    private sealed class StoredOrderHeldUntilReleased : IOrderRepository
    {
        private readonly TaskCompletionSource orderStored = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource storedOrderReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task OrderStored => orderStored.Task;

        public void ReleaseTheStoredOrder() => storedOrderReleased.SetResult();

        public Task<Order?> FindOrderByClOrdIdAsync(string clOrdId, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(null);

        public async Task<bool> TryAddOrderAsync(Order answeredOrder, CancellationToken cancellationToken = default)
        {
            orderStored.SetResult();
            await storedOrderReleased.Task;
            return true;
        }

        public Task DeleteAllOrdersAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class SymbolExposureAlwaysWithinLimit : IExposureRepository
    {
        public Task<bool> TryMoveSymbolExposureWithinLimitAsync(
            string orderSymbol, decimal exposureDelta, decimal exposureLimit, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task ZeroSymbolExposuresAsync(IReadOnlyList<string> orderSymbols, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class UnitOfWorkWithoutDatabase : IUnitOfWork
    {
        public Task BeginTransactionAsync(IsolationLevel transactionIsolationLevel, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task CommitTransactionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RollbackTransactionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static DecideIncomingOrderResponse CreateAcceptedBuyOrderDecision(string symbol, decimal quantity, decimal price) =>
        new(Guid.NewGuid().ToString("N"), "order", "execution", symbol, OrderSideCodes.BuyOrderSideFixCode, quantity, price,
            Accepted: true, RejectReason: null, RejectedForInvalidFields: false, IsRepeat: false);
}
