using Flowa.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderAccumulator.Exposure;
using OrderAccumulator.Observabilidade;
using Xunit.Abstractions;

namespace OrderAccumulator.Tests;

// CA-30 e CA-31: ordem e "Deletar tudo" não se intercalam, e no fim banco e memória dizem a mesma exposição.
[Collection(OrderAccumulatorPostgresCollection.Name)]
public sealed class DeleteAllOrdersConcurrencyTests(OrderAccumulatorPostgresFixture orderAccumulatorDatabase, ITestOutputHelper concurrencyTestOutput)
{
    private const int OrdersPerWave = 100;
    private const int StoredOrdersBeforeFiringTheDeletes = 30;
    private const int SimultaneousDeletesPerRound = 5;

    // Tempo para um passo que não deveria acontecer ter acontecido, se a porta estivesse aberta.
    private static readonly TimeSpan TimeForABlockedStepToSneakIn = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ConcurrencyStepDeadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Delete_waits_for_the_order_in_progress_and_zeroes_the_memory_after_it()
    {
        var symbolExposureMemory = new SymbolExposureMemory();
        var orderInsideTheDoor = new TaskCompletionSource();
        var releaseTheOrder = new TaskCompletionSource();
        var orderInProgress = symbolExposureMemory.ProcessOrderOutsideDeleteAllAsync(async () =>
        {
            orderInsideTheDoor.SetResult();
            await releaseTheOrder.Task;
            var acceptedOrder = NewAcceptedBuyOrderOutcome("PETR4", 100, 10.00m);
            symbolExposureMemory.ApplyAcceptedOrder(acceptedOrder);
            return acceptedOrder;
        }, CancellationToken.None);
        await orderInsideTheDoor.Task.WaitAsync(ConcurrencyStepDeadline);

        var storedDeleteStarted = false;
        var deleteAllOrdersTask = symbolExposureMemory.DeleteAllOrdersAndZeroExposuresAsync(() =>
        {
            storedDeleteStarted = true;
            return Task.CompletedTask;
        }, CancellationToken.None);
        await Task.Delay(TimeForABlockedStepToSneakIn);
        var storedDeleteStartedWhileTheOrderWasInside = storedDeleteStarted;
        releaseTheOrder.SetResult();
        await Task.WhenAll(orderInProgress, deleteAllOrdersTask).WaitAsync(ConcurrencyStepDeadline);

        Assert.False(storedDeleteStartedWhileTheOrderWasInside);
        Assert.True(storedDeleteStarted);
        Assert.Equal([new("PETR4", 0m), new("VALE3", 0m), new("VIIA4", 0m)], symbolExposureMemory.CurrentSymbolExposures());
    }

    [Fact]
    public async Task Order_arriving_during_a_delete_waits_and_adds_up_from_zero_after_it()
    {
        var symbolExposureMemory = new SymbolExposureMemory();
        symbolExposureMemory.LoadStoredExposures([new SymbolExposure("PETR4", 5_000m)]);
        var deleteInsideTheDoor = new TaskCompletionSource();
        var releaseTheDelete = new TaskCompletionSource();
        var deleteAllOrdersTask = symbolExposureMemory.DeleteAllOrdersAndZeroExposuresAsync(async () =>
        {
            deleteInsideTheDoor.SetResult();
            await releaseTheDelete.Task;
        }, CancellationToken.None);
        await deleteInsideTheDoor.Task.WaitAsync(ConcurrencyStepDeadline);

        var orderProcessingStarted = false;
        var orderArrivingDuringDelete = symbolExposureMemory.ProcessOrderOutsideDeleteAllAsync(() =>
        {
            orderProcessingStarted = true;
            var acceptedOrder = NewAcceptedBuyOrderOutcome("PETR4", 100, 10.00m);
            symbolExposureMemory.ApplyAcceptedOrder(acceptedOrder);
            return Task.FromResult(acceptedOrder);
        }, CancellationToken.None);
        await Task.Delay(TimeForABlockedStepToSneakIn);
        var orderStartedWhileTheDeleteWasInside = orderProcessingStarted;
        releaseTheDelete.SetResult();
        await Task.WhenAll(deleteAllOrdersTask, orderArrivingDuringDelete).WaitAsync(ConcurrencyStepDeadline);

        Assert.False(orderStartedWhileTheDeleteWasInside);
        Assert.Equal([new("PETR4", 1_000.00m), new("VALE3", 0m), new("VIIA4", 0m)], symbolExposureMemory.CurrentSymbolExposures());
    }

    [Fact]
    public async Task Orders_still_run_side_by_side_when_no_delete_is_waiting()
    {
        var symbolExposureMemory = new SymbolExposureMemory();
        var firstOrderInside = new TaskCompletionSource();
        var secondOrderInside = new TaskCompletionSource();

        var firstOrderTask = symbolExposureMemory.ProcessOrderOutsideDeleteAllAsync(async () =>
        {
            firstOrderInside.SetResult();
            await secondOrderInside.Task;
            return NewAcceptedBuyOrderOutcome("PETR4", 1, 1.00m);
        }, CancellationToken.None);
        var secondOrderTask = symbolExposureMemory.ProcessOrderOutsideDeleteAllAsync(async () =>
        {
            secondOrderInside.SetResult();
            await firstOrderInside.Task;
            return NewAcceptedBuyOrderOutcome("VALE3", 1, 1.00m);
        }, CancellationToken.None);

        await Task.WhenAll(firstOrderTask, secondOrderTask).WaitAsync(ConcurrencyStepDeadline);
    }

    // Uma fila contínua de ordens não pode deixar o apagar esperando para sempre: ordem nova que chega
    // depois do apagar espera ele passar.
    [Fact]
    public async Task Order_arriving_after_a_waiting_delete_runs_only_after_the_delete()
    {
        var symbolExposureMemory = new SymbolExposureMemory();
        var firstOrderInside = new TaskCompletionSource();
        var releaseTheFirstOrder = new TaskCompletionSource();
        var firstOrderTask = symbolExposureMemory.ProcessOrderOutsideDeleteAllAsync(async () =>
        {
            firstOrderInside.SetResult();
            await releaseTheFirstOrder.Task;
            return NewAcceptedBuyOrderOutcome("PETR4", 1, 1.00m);
        }, CancellationToken.None);
        await firstOrderInside.Task.WaitAsync(ConcurrencyStepDeadline);

        var executedStepsInOrder = new List<string>();
        var deleteAllOrdersTask = symbolExposureMemory.DeleteAllOrdersAndZeroExposuresAsync(() =>
        {
            lock (executedStepsInOrder) executedStepsInOrder.Add("apagar");
            return Task.CompletedTask;
        }, CancellationToken.None);
        await Task.Delay(TimeForABlockedStepToSneakIn);
        var laterOrderTask = symbolExposureMemory.ProcessOrderOutsideDeleteAllAsync(() =>
        {
            lock (executedStepsInOrder) executedStepsInOrder.Add("ordem que chegou depois");
            return Task.FromResult(NewAcceptedBuyOrderOutcome("VALE3", 1, 1.00m));
        }, CancellationToken.None);
        await Task.Delay(TimeForABlockedStepToSneakIn);
        var stepsWhileTheFirstOrderWasInside = executedStepsInOrder.ToList();
        releaseTheFirstOrder.SetResult();
        await Task.WhenAll(firstOrderTask, deleteAllOrdersTask, laterOrderTask).WaitAsync(ConcurrencyStepDeadline);

        Assert.Empty(stepsWhileTheFirstOrderWasInside);
        Assert.Equal(["apagar", "ordem que chegou depois"], executedStepsInOrder);
    }

    // Pelo OrderProcessorWithMetrics de verdade: a ordem já gravada só sai da porta depois de somar na memória.
    // Se a soma ficasse fora da porta, o apagar que espera veria a memória sem a ordem e o zero seria desfeito
    // pela soma atrasada. Repete para a janela entre gravar e somar ser exercitada várias vezes.
    [Fact]
    public async Task Order_through_the_metrics_processor_adds_to_memory_before_a_waiting_delete_runs()
    {
        using var orderMetricsClient = OrderMetricsSetup.CreateOrderMetricsClient(FindDogStatsdPortWithoutListener(), new ConfigurationBuilder().Build());
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var symbolExposureMemory = new SymbolExposureMemory();
            var storedOrderHeldUntilReleased = new StoredOrderHeldUntilReleased(NewAcceptedBuyOrderOutcome("PETR4", 100, 10.00m));
            var orderProcessorWithMetrics = new OrderProcessorWithMetrics(storedOrderHeldUntilReleased, orderMetricsClient, symbolExposureMemory);

            var orderInProgress = orderProcessorWithMetrics.ProcessIncomingOrderAsync(TestOrders.NewBuyOrder("PETR4", 100, 10.00m));
            await storedOrderHeldUntilReleased.OrderStored.WaitAsync(ConcurrencyStepDeadline);
            IReadOnlyList<SymbolExposure>? exposureMemorySeenByTheDelete = null;
            var deleteAllOrdersTask = symbolExposureMemory.DeleteAllOrdersAndZeroExposuresAsync(() =>
            {
                exposureMemorySeenByTheDelete = symbolExposureMemory.CurrentSymbolExposures();
                return Task.CompletedTask;
            }, CancellationToken.None);
            await Task.Delay(20);
            storedOrderHeldUntilReleased.ReleaseTheStoredOrder();
            await Task.WhenAll(orderInProgress, deleteAllOrdersTask).WaitAsync(ConcurrencyStepDeadline);

            Assert.Equal([new("PETR4", 1_000.00m), new("VALE3", 0m), new("VIIA4", 0m)], exposureMemorySeenByTheDelete);
            Assert.Equal([new("PETR4", 0m), new("VALE3", 0m), new("VIIA4", 0m)], symbolExposureMemory.CurrentSymbolExposures());
        }
    }

    // Pelo app inteiro: 100 ordens nos três símbolos; quando 30 já gravaram, 5 apagar pela rota entram no meio das
    // que ainda estão em curso; depois dos apagar, mais 100 ordens. Sem exceção e sem deadlock; no fim sobram ordens
    // (as que entraram depois do último apagar) e a exposição do banco é a da memória e a soma das aceitas que sobraram.
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
        var appOrderProcessor = orderAccumulatorTestApp.Services.GetRequiredService<IOrderProcessor>();
        var appSymbolExposureMemory = orderAccumulatorTestApp.Services.GetRequiredService<SymbolExposureMemory>();
        var orderAccumulatorClient = orderAccumulatorTestApp.CreateClient();
        var orderQuantityGenerator = new Random(concurrencyRound);
        var ordersStoredBeforeTheDeletes = 0;
        var enoughOrdersStoredToFireTheDeletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var ordersBeforeTheDeletes = NewOrdersMixingSymbolsAndSides(OrdersPerWave, orderQuantityGenerator).Select(symbolAndSideMixedOrder => Task.Run(async () =>
        {
            var orderOutcome = await appOrderProcessor.ProcessIncomingOrderAsync(symbolAndSideMixedOrder);
            if (Interlocked.Increment(ref ordersStoredBeforeTheDeletes) == StoredOrdersBeforeFiringTheDeletes)
                enoughOrdersStoredToFireTheDeletes.TrySetResult();
            return orderOutcome;
        })).ToList();
        await enoughOrdersStoredToFireTheDeletes.Task.WaitAsync(TimeSpan.FromMinutes(2));
        var ordersInProgressWhenTheDeletesFired = ordersBeforeTheDeletes.Count(orderBeforeTheDeletes => !orderBeforeTheDeletes.IsCompleted);
        var deleteResponses = await Task.WhenAll(Enumerable.Range(0, SimultaneousDeletesPerRound)
            .Select(_ => Task.Run(() => orderAccumulatorClient.DeleteAsync("/api/orders")))).WaitAsync(TimeSpan.FromMinutes(2));
        var ordersAfterTheDeletes = NewOrdersMixingSymbolsAndSides(OrdersPerWave, orderQuantityGenerator)
            .Select(symbolAndSideMixedOrder => Task.Run(() => appOrderProcessor.ProcessIncomingOrderAsync(symbolAndSideMixedOrder))).ToList();
        var orderOutcomes = await Task.WhenAll(ordersBeforeTheDeletes.Concat(ordersAfterTheDeletes)).WaitAsync(TimeSpan.FromMinutes(2));

        var remainingStoredOrders = await orderAccumulatorDatabase.CountStoredOrdersAsync();
        var storedExposures = await orderAccumulatorDatabase.ExposureReader.GetSymbolExposuresAsync();
        concurrencyTestOutput.WriteLine(
            $"rodada {concurrencyRound}: {ordersInProgressWhenTheDeletesFired} ordens em curso quando os apagar saíram; " +
            $"{remainingStoredOrders} ordens sobraram; " +
            string.Join(", ", storedExposures.Select(storedExposure => $"{storedExposure.Symbol}={storedExposure.Exposure}")));
        Assert.All(orderOutcomes, orderOutcome => Assert.True(orderOutcome.Accepted));
        Assert.All(deleteResponses, deleteResponse => Assert.Equal(System.Net.HttpStatusCode.NoContent, deleteResponse.StatusCode));
        Assert.InRange(ordersInProgressWhenTheDeletesFired, 1, OrdersPerWave - StoredOrdersBeforeFiringTheDeletes);
        Assert.InRange(remainingStoredOrders, OrdersPerWave, 2 * OrdersPerWave - StoredOrdersBeforeFiringTheDeletes);
        Assert.Equal(storedExposures, appSymbolExposureMemory.CurrentSymbolExposures());
        foreach (var storedExposure in storedExposures)
            Assert.Equal(await orderAccumulatorDatabase.SumAcceptedOrdersExposureAsync(storedExposure.Symbol), storedExposure.Exposure);
    }

    private static List<IncomingOrder> NewOrdersMixingSymbolsAndSides(int orderCount, Random orderQuantityGenerator) =>
        Enumerable.Range(0, orderCount)
            .Select(orderNumber => TestOrders.NewIncomingOrder(
                OrderRules.AllowedOrderSymbols[orderNumber % 3],
                orderNumber % 2 == 0 ? OrderSideCodes.BuyOrderSideFixCode : OrderSideCodes.SellOrderSideFixCode,
                orderQuantityGenerator.Next(1, 1_000), 10.00m))
            .ToList();

    private static int FindDogStatsdPortWithoutListener() => OrderAccumulatorFixTestHost.FindFreeFixAcceptorTcpPort();

    // Faz o papel do PostgresOrderProcessor: a ordem "já gravou" e fica parada até o teste soltar.
    private sealed class StoredOrderHeldUntilReleased(OrderOutcome storedOrderOutcome) : IOrderProcessor
    {
        private readonly TaskCompletionSource orderStored = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource storedOrderReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task OrderStored => orderStored.Task;

        public void ReleaseTheStoredOrder() => storedOrderReleased.SetResult();

        public async Task<OrderOutcome> ProcessIncomingOrderAsync(IncomingOrder incomingOrder, CancellationToken cancellationToken = default)
        {
            orderStored.SetResult();
            await storedOrderReleased.Task;
            return storedOrderOutcome;
        }
    }

    private static OrderOutcome NewAcceptedBuyOrderOutcome(string symbol, decimal quantity, decimal price) =>
        new(Guid.NewGuid().ToString("N"), "ordem", "execucao", symbol, OrderSideCodes.BuyOrderSideFixCode, quantity, price,
            Accepted: true, RejectReason: null, IsRepeat: false);
}
