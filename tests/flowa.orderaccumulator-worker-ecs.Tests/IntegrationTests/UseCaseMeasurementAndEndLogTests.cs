using Flowa.OrderAccumulator.Application.Orders.UseCases;
using Flowa.OrderAccumulator.Domain.Orders.Entities;
using Flowa.OrderAccumulator.Domain.Orders.Interfaces;

namespace Flowa.OrderAccumulator.Tests;

// CA-21 and CA-22 for the use case of the worker: the stable operation name, the closed result of success and of failure,
// and the one Information line with the business fact only when the use case ends well (a rejection or a repeat writes
// none here, G-6). The list, the exposure read and the delete are use cases of the Generator now.
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
            orderAccumulatorDatabase.OrderDatabaseConnectionSource,
            operationMonitoring: recordingMonitoring, orderDecisionLogger: recordingLogger);
        var failingOrderDecisionRunner = new DecideIncomingOrderTestRunner(
            orderAccumulatorDatabase.OrderDatabaseConnectionSource,
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

    private sealed class OrderRepositoryAnswering(Exception? orderFailure) : IOrderRepository
    {
        public Task<Order?> FindOrderByClOrdIdAsync(string clOrdId, CancellationToken cancellationToken = default) =>
            orderFailure is null ? Task.FromResult<Order?>(null) : Task.FromException<Order?>(orderFailure);

        public Task<bool> TryAddOrderAsync(Order answeredOrder, CancellationToken cancellationToken = default) =>
            orderFailure is null ? Task.FromResult(true) : Task.FromException<bool>(orderFailure);
    }
}
