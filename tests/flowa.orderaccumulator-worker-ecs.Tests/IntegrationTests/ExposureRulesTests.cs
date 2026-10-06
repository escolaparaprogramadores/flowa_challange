using Flowa.OrderAccumulator.Domain.Exposures.ValueObjects;
using Flowa.OrderAccumulator.Domain.Orders.Enums;
using Flowa.OrderAccumulator.Domain.Orders.ValueObjects;

namespace Flowa.OrderAccumulator.Tests;

[Collection(OrderAccumulatorPostgresCollection.Name)]
public sealed class ExposureRulesTests(OrderAccumulatorPostgresFixture orderAccumulatorDatabase) : IAsyncLifetime
{
    public Task InitializeAsync() => orderAccumulatorDatabase.ResetOrdersAndExposuresAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // CA-5
    [Fact]
    public async Task Buys_add_and_sells_subtract_in_separate_accounts_per_symbol()
    {
        await ProcessExpectingAcceptanceAsync(TestOrders.NewBuyOrder("PETR4", 100, 10.50m));

        Assert.Equal(
            [new SymbolExposure("PETR4", 1_050.00m), new("VALE3", 0m), new("VIIA4", 0m)],
            await orderAccumulatorDatabase.ExposureReader.GetSymbolExposuresAsync());

        await ProcessExpectingAcceptanceAsync(TestOrders.NewBuyOrder("VALE3", 200, 20.00m));
        await ProcessExpectingAcceptanceAsync(TestOrders.NewSellOrder("VIIA4", 300, 5.25m));
        await ProcessExpectingAcceptanceAsync(TestOrders.NewSellOrder("PETR4", 50, 10.00m));
        await ProcessExpectingAcceptanceAsync(TestOrders.NewSellOrder("VALE3", 100, 20.00m));
        await ProcessExpectingAcceptanceAsync(TestOrders.NewBuyOrder("VIIA4", 100, 1.00m));

        // PETR4: 1.050 - 500 · VALE3: 4.000 - 2.000 · VIIA4: -1.575 + 100
        Assert.Equal(
            [new SymbolExposure("PETR4", 550.00m), new("VALE3", 2_000.00m), new("VIIA4", -1_475.00m)],
            await orderAccumulatorDatabase.ExposureReader.GetSymbolExposuresAsync());
    }

    // CA-6: 2 × 49,999,500.00 + 1,000.00 lands exactly on the limit, upwards and downwards.
    [Theory]
    [InlineData(OrderSideCodes.BuyOrderSideFixCode, 1)]
    [InlineData(OrderSideCodes.SellOrderSideFixCode, -1)]
    public async Task Order_that_lands_exactly_on_the_limit_is_accepted(char orderSide, int limitSign)
    {
        await ProcessExpectingAcceptanceAsync(TestOrders.NewIncomingOrder("PETR4", orderSide, 50_000, 999.99m));
        await ProcessExpectingAcceptanceAsync(TestOrders.NewIncomingOrder("PETR4", orderSide, 50_000, 999.99m));

        var orderLandingOnTheLimit = await orderAccumulatorDatabase.OrderDecisionRunner.DecideIncomingOrderAsync(
            TestOrders.NewIncomingOrder("PETR4", orderSide, 1_000, 1.00m));

        Assert.True(orderLandingOnTheLimit.Accepted);
        Assert.Null(orderLandingOnTheLimit.RejectReason);
        Assert.Equal(limitSign * ExposureLimitPolicy.PerSymbol, await orderAccumulatorDatabase.ReadExposureOfSymbolAsync("PETR4"));
    }

    // CA-7: with 999.99 left to the limit, an order of 1,000.00 goes one cent over and is rejected;
    // the next one, of 999.99, fits from the balance before.
    [Theory]
    [InlineData(OrderSideCodes.BuyOrderSideFixCode, 1)]
    [InlineData(OrderSideCodes.SellOrderSideFixCode, -1)]
    public async Task Order_that_passes_the_limit_is_rejected_and_leaves_the_exposure_as_it_was(char orderSide, int limitSign)
    {
        await ProcessExpectingAcceptanceAsync(TestOrders.NewIncomingOrder("VALE3", orderSide, 50_000, 999.99m));
        await ProcessExpectingAcceptanceAsync(TestOrders.NewIncomingOrder("VALE3", orderSide, 50_000, 999.99m));
        await ProcessExpectingAcceptanceAsync(TestOrders.NewIncomingOrder("VALE3", orderSide, 1, 0.01m));

        var exposureBeforeRejection = await orderAccumulatorDatabase.ReadExposureOfSymbolAsync("VALE3");
        Assert.Equal(limitSign * (ExposureLimitPolicy.PerSymbol - 999.99m), exposureBeforeRejection);

        var orderOneCentPastTheLimit = await orderAccumulatorDatabase.OrderDecisionRunner.DecideIncomingOrderAsync(
            TestOrders.NewIncomingOrder("VALE3", orderSide, 10, 100.00m));

        Assert.False(orderOneCentPastTheLimit.Accepted);
        Assert.Equal(ExposureLimitPolicy.BuildExposureLimitRejectionText("VALE3"), orderOneCentPastTheLimit.RejectReason);
        Assert.Equal(exposureBeforeRejection, await orderAccumulatorDatabase.ReadExposureOfSymbolAsync("VALE3"));

        await ProcessExpectingAcceptanceAsync(TestOrders.NewIncomingOrder("VALE3", orderSide, 1, 999.99m));
        Assert.Equal(limitSign * ExposureLimitPolicy.PerSymbol, await orderAccumulatorDatabase.ReadExposureOfSymbolAsync("VALE3"));
    }

    [Fact]
    public async Task At_the_limit_an_order_in_the_other_direction_is_still_accepted()
    {
        await ProcessExpectingAcceptanceAsync(TestOrders.NewBuyOrder("VIIA4", 50_000, 999.99m));
        await ProcessExpectingAcceptanceAsync(TestOrders.NewBuyOrder("VIIA4", 50_000, 999.99m));
        await ProcessExpectingAcceptanceAsync(TestOrders.NewBuyOrder("VIIA4", 1_000, 1.00m));

        await ProcessExpectingAcceptanceAsync(TestOrders.NewSellOrder("VIIA4", 10, 100.00m));

        Assert.Equal(ExposureLimitPolicy.PerSymbol - 1_000m, await orderAccumulatorDatabase.ReadExposureOfSymbolAsync("VIIA4"));
    }

    private async Task ProcessExpectingAcceptanceAsync(IncomingOrder incomingOrder)
    {
        var orderDecision = await orderAccumulatorDatabase.OrderDecisionRunner.DecideIncomingOrderAsync(incomingOrder);
        Assert.True(orderDecision.Accepted,
            $"order {incomingOrder.Symbol} {incomingOrder.Side} {incomingOrder.Quantity} x {incomingOrder.Price} was rejected: {orderDecision.RejectReason}");
    }
}
