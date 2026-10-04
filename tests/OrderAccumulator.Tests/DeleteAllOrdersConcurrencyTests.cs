using Flowa.Shared;
using Microsoft.Extensions.DependencyInjection;
using OrderAccumulator.Exposure;
using OrderAccumulator.Observabilidade;
using Xunit.Abstractions;

namespace OrderAccumulator.Tests;

// CA-30 e CA-31: ordem e "Deletar tudo" não se intercalam, e no fim banco e memória dizem a mesma exposição.
[Collection(OrderAccumulatorPostgresCollection.Name)]
public sealed class DeleteAllOrdersConcurrencyTests(OrderAccumulatorPostgresFixture orderAccumulatorDatabase, ITestOutputHelper concurrencyTestOutput)
{
    private const int SimultaneousOrdersPerRound = 200;
    private const int SimultaneousDeletesPerRound = 5;

    // Tempo para um passo que não deveria acontecer ter acontecido, se a porta estivesse aberta.
    private static readonly TimeSpan BlockedStepWindow = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan StepDeadline = TimeSpan.FromSeconds(10);

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
            var acceptedOrder = AcceptedBuyOutcome("PETR4", 100, 10.00m);
            symbolExposureMemory.ApplyAcceptedOrder(acceptedOrder);
            return acceptedOrder;
        }, CancellationToken.None);
        await orderInsideTheDoor.Task.WaitAsync(StepDeadline);

        var storedDeleteStarted = false;
        var deleteAll = symbolExposureMemory.DeleteAllOrdersAndZeroExposuresAsync(() =>
        {
            storedDeleteStarted = true;
            return Task.CompletedTask;
        }, CancellationToken.None);
        await Task.Delay(BlockedStepWindow);
        var storedDeleteStartedWhileTheOrderWasInside = storedDeleteStarted;
        releaseTheOrder.SetResult();
        await Task.WhenAll(orderInProgress, deleteAll).WaitAsync(StepDeadline);

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
        var deleteAll = symbolExposureMemory.DeleteAllOrdersAndZeroExposuresAsync(async () =>
        {
            deleteInsideTheDoor.SetResult();
            await releaseTheDelete.Task;
        }, CancellationToken.None);
        await deleteInsideTheDoor.Task.WaitAsync(StepDeadline);

        var orderProcessingStarted = false;
        var orderArrivingDuringDelete = symbolExposureMemory.ProcessOrderOutsideDeleteAllAsync(() =>
        {
            orderProcessingStarted = true;
            var acceptedOrder = AcceptedBuyOutcome("PETR4", 100, 10.00m);
            symbolExposureMemory.ApplyAcceptedOrder(acceptedOrder);
            return Task.FromResult(acceptedOrder);
        }, CancellationToken.None);
        await Task.Delay(BlockedStepWindow);
        var orderStartedWhileTheDeleteWasInside = orderProcessingStarted;
        releaseTheDelete.SetResult();
        await Task.WhenAll(deleteAll, orderArrivingDuringDelete).WaitAsync(StepDeadline);

        Assert.False(orderStartedWhileTheDeleteWasInside);
        Assert.Equal([new("PETR4", 1_000.00m), new("VALE3", 0m), new("VIIA4", 0m)], symbolExposureMemory.CurrentSymbolExposures());
    }

    [Fact]
    public async Task Orders_still_run_side_by_side_when_no_delete_is_waiting()
    {
        var symbolExposureMemory = new SymbolExposureMemory();
        var firstOrderInside = new TaskCompletionSource();
        var secondOrderInside = new TaskCompletionSource();

        var firstOrder = symbolExposureMemory.ProcessOrderOutsideDeleteAllAsync(async () =>
        {
            firstOrderInside.SetResult();
            await secondOrderInside.Task;
            return AcceptedBuyOutcome("PETR4", 1, 1.00m);
        }, CancellationToken.None);
        var secondOrder = symbolExposureMemory.ProcessOrderOutsideDeleteAllAsync(async () =>
        {
            secondOrderInside.SetResult();
            await firstOrderInside.Task;
            return AcceptedBuyOutcome("VALE3", 1, 1.00m);
        }, CancellationToken.None);

        await Task.WhenAll(firstOrder, secondOrder).WaitAsync(StepDeadline);
    }

    // Uma fila contínua de ordens não pode deixar o apagar esperando para sempre: ordem nova que chega
    // depois do apagar espera ele passar.
    [Fact]
    public async Task Order_arriving_after_a_waiting_delete_runs_only_after_the_delete()
    {
        var symbolExposureMemory = new SymbolExposureMemory();
        var firstOrderInside = new TaskCompletionSource();
        var releaseTheFirstOrder = new TaskCompletionSource();
        var firstOrder = symbolExposureMemory.ProcessOrderOutsideDeleteAllAsync(async () =>
        {
            firstOrderInside.SetResult();
            await releaseTheFirstOrder.Task;
            return AcceptedBuyOutcome("PETR4", 1, 1.00m);
        }, CancellationToken.None);
        await firstOrderInside.Task.WaitAsync(StepDeadline);

        var stepsInOrder = new List<string>();
        var deleteAll = symbolExposureMemory.DeleteAllOrdersAndZeroExposuresAsync(() =>
        {
            lock (stepsInOrder) stepsInOrder.Add("apagar");
            return Task.CompletedTask;
        }, CancellationToken.None);
        await Task.Delay(BlockedStepWindow);
        var laterOrder = symbolExposureMemory.ProcessOrderOutsideDeleteAllAsync(() =>
        {
            lock (stepsInOrder) stepsInOrder.Add("ordem que chegou depois");
            return Task.FromResult(AcceptedBuyOutcome("VALE3", 1, 1.00m));
        }, CancellationToken.None);
        await Task.Delay(BlockedStepWindow);
        var stepsWhileTheFirstOrderWasInside = stepsInOrder.ToList();
        releaseTheFirstOrder.SetResult();
        await Task.WhenAll(firstOrder, deleteAll, laterOrder).WaitAsync(StepDeadline);

        Assert.Empty(stepsWhileTheFirstOrderWasInside);
        Assert.Equal(["apagar", "ordem que chegou depois"], stepsInOrder);
    }

    // 200 ordens nos três símbolos e 5 apagar pela rota, todos ao mesmo tempo, pelo app inteiro. Sem exceção
    // e sem deadlock; no fim a exposição do banco é a da memória e é a soma das ordens aceitas que sobraram.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task Simultaneous_orders_and_deletes_end_with_the_same_exposure_in_the_database_and_in_memory(int concurrencyRound)
    {
        await orderAccumulatorDatabase.ResetOrdersAndExposuresAsync();
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();
        var appOrderProcessor = orderAccumulatorTestApp.Services.GetRequiredService<IOrderProcessor>();
        var appSymbolExposureMemory = orderAccumulatorTestApp.Services.GetRequiredService<SymbolExposureMemory>();
        var orderAccumulatorClient = orderAccumulatorTestApp.CreateClient();
        var orderQuantityGenerator = new Random(concurrencyRound);
        var simultaneousOrders = Enumerable.Range(0, SimultaneousOrdersPerRound)
            .Select(orderNumber => TestOrders.NewIncomingOrder(
                OrderRules.AllowedOrderSymbols[orderNumber % 3],
                orderNumber % 2 == 0 ? OrderSideCodes.BuyOrderSideFixCode : OrderSideCodes.SellOrderSideFixCode,
                orderQuantityGenerator.Next(1, 1_000), 10.00m))
            .ToList();

        var orderTasks = simultaneousOrders.Select(simultaneousOrder => Task.Run(() => appOrderProcessor.ProcessIncomingOrderAsync(simultaneousOrder))).ToList();
        var deleteTasks = Enumerable.Range(0, SimultaneousDeletesPerRound)
            .Select(_ => Task.Run(() => orderAccumulatorClient.DeleteAsync("/api/orders")))
            .ToList();
        var orderOutcomes = await Task.WhenAll(orderTasks).WaitAsync(TimeSpan.FromMinutes(2));
        var deleteResponses = await Task.WhenAll(deleteTasks).WaitAsync(TimeSpan.FromMinutes(2));

        Assert.All(orderOutcomes, orderOutcome => Assert.True(orderOutcome.Accepted));
        Assert.All(deleteResponses, deleteResponse => Assert.Equal(System.Net.HttpStatusCode.NoContent, deleteResponse.StatusCode));
        var storedExposures = await orderAccumulatorDatabase.ExposureReader.GetSymbolExposuresAsync();
        Assert.Equal(storedExposures, appSymbolExposureMemory.CurrentSymbolExposures());
        foreach (var storedExposure in storedExposures)
            Assert.Equal(await orderAccumulatorDatabase.SumAcceptedOrdersExposureAsync(storedExposure.Symbol), storedExposure.Exposure);
        concurrencyTestOutput.WriteLine(
            $"rodada {concurrencyRound}: {await orderAccumulatorDatabase.CountStoredOrdersAsync()} ordens sobraram depois dos apagar; " +
            string.Join(", ", storedExposures.Select(storedExposure => $"{storedExposure.Symbol}={storedExposure.Exposure}")));
    }

    private static OrderOutcome AcceptedBuyOutcome(string symbol, decimal quantity, decimal price) =>
        new(Guid.NewGuid().ToString("N"), "ordem", "execucao", symbol, OrderSideCodes.BuyOrderSideFixCode, quantity, price,
            Accepted: true, RejectReason: null, IsRepeat: false);
}
