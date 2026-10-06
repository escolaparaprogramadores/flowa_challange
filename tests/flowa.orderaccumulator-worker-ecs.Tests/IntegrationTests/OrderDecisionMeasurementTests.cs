using System.Diagnostics;
using Flowa.Commons.Observability;

namespace Flowa.OrderAccumulator.Tests;

// CA-22 on the FIX path: the order use case tags the span already open by the receiving (fix.recebimento_da_ordem)
// with the closed result of the decision, and a rejected order never marks the span as an error.
[Collection(OrderAccumulatorPostgresCollection.Name)]
public sealed class OrderDecisionMeasurementTests(OrderAccumulatorPostgresFixture orderAccumulatorDatabase) : IAsyncLifetime
{
    private const string OrderReceivingTestSourceName = "Base.OrderAccumulator.Tests.OrderReceiving";

    private static readonly ActivitySource OrderReceivingTestSource = new(OrderReceivingTestSourceName);

    private readonly ActivityListener orderReceivingListener = new()
    {
        ShouldListenTo = traceSource => traceSource.Name == OrderReceivingTestSourceName,
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
    };

    private DecideIncomingOrderTestRunner orderDecisionRunner = null!;

    public async Task InitializeAsync()
    {
        await orderAccumulatorDatabase.ResetOrdersAndExposuresAsync();
        ActivitySource.AddActivityListener(orderReceivingListener);
        orderDecisionRunner = new DecideIncomingOrderTestRunner(
            orderAccumulatorDatabase.OrderDatabaseConnectionSource, new UncountedOrderMetrics());
    }

    public Task DisposeAsync()
    {
        orderReceivingListener.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Accepted_rejected_and_repeated_orders_tag_the_open_span_with_their_result()
    {
        var acceptedSale = TestOrders.NewSellOrder("VIIA4", 99999, 999.99m);

        var acceptedOrderSpan = await DecideInsideOpenSpanAsync(acceptedSale);
        var repeatedOrderSpan = await DecideInsideOpenSpanAsync(acceptedSale);
        var orderOverTheLimitSpan = await DecideInsideOpenSpanAsync(TestOrders.NewSellOrder("VIIA4", 99999, 999.99m));
        var orderWithInvalidSymbolSpan = await DecideInsideOpenSpanAsync(TestOrders.NewBuyOrder("ITUB4", 100, 10.00m));

        Assert.Equal(
            ["accepted", "repeated", "rejected", "rejected"],
            new[] { acceptedOrderSpan, repeatedOrderSpan, orderOverTheLimitSpan, orderWithInvalidSymbolSpan }
                .Select(orderSpan => orderSpan.GetTagItem(ActiveSpanOperationMonitoring.OperationResultTag)));
        Assert.All(
            new[] { acceptedOrderSpan, repeatedOrderSpan, orderOverTheLimitSpan, orderWithInvalidSymbolSpan },
            orderSpan =>
            {
                Assert.Equal("orders.decide-incoming-order", orderSpan.GetTagItem(ActiveSpanOperationMonitoring.OperationNameTag));
                Assert.IsType<double>(orderSpan.GetTagItem(ActiveSpanOperationMonitoring.OperationDurationTag));
                Assert.Equal(ActivityStatusCode.Unset, orderSpan.Status);
            });
    }

    private async Task<Activity> DecideInsideOpenSpanAsync(Domain.Orders.ValueObjects.IncomingOrder incomingOrder)
    {
        using var orderReceivingSpan = OrderReceivingTestSource.StartActivity("fix.recebimento_da_ordem")!;
        await orderDecisionRunner.DecideIncomingOrderAsync(incomingOrder);
        return orderReceivingSpan;
    }
}
