using System.Net.Http.Json;
using System.Text.Json;
using Flowa.OrderAccumulator.Application.Exposures.Responses;
using Flowa.OrderAccumulator.Domain.Exposures.ValueObjects;
using Flowa.OrderAccumulator.Domain.Orders.Enums;
using QuickFix.Fields;
using QuickFix.FIX44;
using Xunit.Abstractions;

namespace Flowa.OrderAccumulator.Tests;

[Collection(OrderAccumulatorPostgresCollection.Name)]
public sealed class FixConcurrentOrderExposureTests(OrderAccumulatorPostgresFixture orderAccumulatorDatabase, ITestOutputHelper concurrencyTestOutput)
    : IAsyncLifetime
{
    private const int SimultaneousFixOrders = 200;
    private const string ConcurrencySymbol = "PETR4";
    private const decimal ConcurrencyOrderPrice = 20.00m;

    private static readonly TimeSpan AllExecutionReportsTimeout = TimeSpan.FromSeconds(60);

    public Task InitializeAsync() => orderAccumulatorDatabase.ResetOrdersAndExposuresAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(OrderSideCodes.BuyOrderSideFixCode)]
    [InlineData(OrderSideCodes.SellOrderSideFixCode)]
    public async Task Two_hundred_simultaneous_fix_orders_on_one_symbol_never_push_the_exposure_past_the_limit(char orderSide)
    {
        // Arrange
        // CA-21: the orders go through a FIX session (test initiator → acceptor of the whole app), never straight to the use case.
        // The 200 are sent at the same time, but the acceptor decides the orders of one session one at a time; the
        // database transactions waiting together on the exposure row are proved by ConcurrentOrderExposureTests.
        // Quantities from 5,000 to 99,999 at 20.00: about 1 million per order, close to 2× the limit in total, so part of
        // them must be rejected.
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();
        using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort);
        var orderQuantityGenerator = new Random(orderSide);
        var simultaneousFixOrders = Enumerable.Range(0, SimultaneousFixOrders)
            .Select(orderNumber => FixTestInitiator.NewOrder(
                $"fix-concurrency-{orderSide}-{orderNumber}", ConcurrencySymbol, orderSide, orderQuantityGenerator.Next(5_000, 100_000), ConcurrencyOrderPrice))
            .ToList();

        // Act
        var executionReportsByClOrdId = await fixTestInitiator.SendAllAtOnceExpectingExecutionReportsAsync(simultaneousFixOrders, AllExecutionReportsTimeout);

        // Assert
        var acceptedFixOrders = simultaneousFixOrders
            .Where(fixOrder => executionReportsByClOrdId[fixOrder.ClOrdID.Value].ExecType.Value == ExecType.NEW).ToList();
        var rejectedFixOrders = simultaneousFixOrders
            .Where(fixOrder => executionReportsByClOrdId[fixOrder.ClOrdID.Value].ExecType.Value == ExecType.REJECTED).ToList();
        var finalExposure = await orderAccumulatorDatabase.ReadExposureOfSymbolAsync(ConcurrencySymbol);
        var exposureShownByTheApp = await ReadExposureShownByTheAppAsync(orderAccumulatorTestApp, ConcurrencySymbol);
        concurrencyTestOutput.WriteLine(
            $"side {orderSide}: {SimultaneousFixOrders} orders sent at once by FIX, {executionReportsByClOrdId.Count} ExecutionReports, " +
            $"{acceptedFixOrders.Count} accepted, {rejectedFixOrders.Count} rejected, final exposure {finalExposure}, " +
            $"exposure in GET /api/exposures {exposureShownByTheApp}");

        Assert.Equal(SimultaneousFixOrders, executionReportsByClOrdId.Count);
        Assert.Equal(SimultaneousFixOrders, acceptedFixOrders.Count + rejectedFixOrders.Count);
        Assert.Equal(acceptedFixOrders.Sum(CalculateExposureDeltaOfFixOrder), finalExposure);
        Assert.Equal(finalExposure, await orderAccumulatorDatabase.SumAcceptedOrdersExposureAsync(ConcurrencySymbol));
        Assert.Equal(finalExposure, exposureShownByTheApp);
        // Each round goes in one direction only, so the exposure only grows in absolute value: the final one within the
        // limit means no accepted order along the way took it past the limit.
        Assert.True(Math.Abs(finalExposure) <= ExposureLimitPolicy.PerSymbol, $"exposure {finalExposure} went over the limit");
        Assert.Equal(SimultaneousFixOrders, await orderAccumulatorDatabase.CountStoredOrdersAsync());
        Assert.NotEmpty(rejectedFixOrders);
        Assert.All(rejectedFixOrders, rejectedFixOrder => Assert.Equal(
            ExposureLimitPolicy.BuildExposureLimitRejectionText(ConcurrencySymbol), executionReportsByClOrdId[rejectedFixOrder.ClOrdID.Value].Text.Value));
        // Every rejected order was larger than the room left at the end: the accepted ones reached the limit.
        Assert.All(rejectedFixOrders, rejectedFixOrder => Assert.True(
            Math.Abs(CalculateExposureDeltaOfFixOrder(rejectedFixOrder)) > ExposureLimitPolicy.CalculateRemainingExposureCapacity(finalExposure)));
    }

    private static decimal CalculateExposureDeltaOfFixOrder(NewOrderSingle fixOrder) =>
        ExposureLimitPolicy.CalculateAcceptedOrderExposureDelta(fixOrder.Side.Value, fixOrder.OrderQty.Value, fixOrder.Price.Value);

    private static async Task<decimal> ReadExposureShownByTheAppAsync(OrderAccumulatorFixTestHost orderAccumulatorTestApp, string symbol)
    {
        var exposuresDataMessage = await orderAccumulatorTestApp.CreateClient().GetFromJsonAsync<JsonElement>("/api/exposures");
        var shownExposures = exposuresDataMessage.GetProperty("data").Deserialize<ExposuresResponse>(JsonSerializerOptions.Web)!;
        return shownExposures.Exposures.Single(symbolExposure => symbolExposure.Symbol == symbol).Exposure;
    }
}
