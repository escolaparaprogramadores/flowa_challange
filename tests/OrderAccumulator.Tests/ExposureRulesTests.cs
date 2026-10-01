using Flowa.Shared;
using OrderAccumulator.Exposure;

namespace OrderAccumulator.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ExposureRulesTests(PostgresFixture postgres) : IAsyncLifetime
{
    public Task InitializeAsync() => postgres.ResetOrdersAndExposuresAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // CA-5
    [Fact]
    public async Task Buys_add_and_sells_subtract_in_separate_accounts_per_symbol()
    {
        await ProcessExpectingAcceptanceAsync(TestOrders.NewBuyOrder("PETR4", 100, 10.50m));

        Assert.Equal(
            [new SymbolExposure("PETR4", 1_050.00m), new("VALE3", 0m), new("VIIA4", 0m)],
            await postgres.ExposureReader.GetSymbolExposuresAsync());

        await ProcessExpectingAcceptanceAsync(TestOrders.NewBuyOrder("VALE3", 200, 20.00m));
        await ProcessExpectingAcceptanceAsync(TestOrders.NewSellOrder("VIIA4", 300, 5.25m));
        await ProcessExpectingAcceptanceAsync(TestOrders.NewSellOrder("PETR4", 50, 10.00m));
        await ProcessExpectingAcceptanceAsync(TestOrders.NewSellOrder("VALE3", 100, 20.00m));
        await ProcessExpectingAcceptanceAsync(TestOrders.NewBuyOrder("VIIA4", 100, 1.00m));

        // PETR4: 1.050 - 500 · VALE3: 4.000 - 2.000 · VIIA4: -1.575 + 100
        Assert.Equal(
            [new SymbolExposure("PETR4", 550.00m), new("VALE3", 2_000.00m), new("VIIA4", -1_475.00m)],
            await postgres.ExposureReader.GetSymbolExposuresAsync());
    }

    // CA-6: 2 × 49.999.500,00 + 1.000,00 cai exatamente no limite, para cima e para baixo.
    [Theory]
    [InlineData(OrderSideCodes.BuyOrderSideFixCode, 1)]
    [InlineData(OrderSideCodes.SellOrderSideFixCode, -1)]
    public async Task Order_that_lands_exactly_on_the_limit_is_accepted(char orderSide, int limitSign)
    {
        await ProcessExpectingAcceptanceAsync(TestOrders.NewIncomingOrder("PETR4", orderSide, 50_000, 999.99m));
        await ProcessExpectingAcceptanceAsync(TestOrders.NewIncomingOrder("PETR4", orderSide, 50_000, 999.99m));

        var orderLandingOnTheLimit = await postgres.OrderProcessor.ProcessIncomingOrderAsync(
            TestOrders.NewIncomingOrder("PETR4", orderSide, 1_000, 1.00m));

        Assert.True(orderLandingOnTheLimit.Accepted);
        Assert.Null(orderLandingOnTheLimit.RejectReason);
        Assert.Equal(limitSign * ExposureLimit.PerSymbol, await postgres.ReadExposureOfSymbolAsync("PETR4"));
    }

    // CA-7: faltando 999,99 para o limite, uma ordem de 1.000,00 passa um centavo e é rejeitada;
    // a seguinte, de 999,99, cabe a partir do saldo de antes.
    [Theory]
    [InlineData(OrderSideCodes.BuyOrderSideFixCode, 1)]
    [InlineData(OrderSideCodes.SellOrderSideFixCode, -1)]
    public async Task Order_that_passes_the_limit_is_rejected_and_leaves_the_exposure_as_it_was(char orderSide, int limitSign)
    {
        await ProcessExpectingAcceptanceAsync(TestOrders.NewIncomingOrder("VALE3", orderSide, 50_000, 999.99m));
        await ProcessExpectingAcceptanceAsync(TestOrders.NewIncomingOrder("VALE3", orderSide, 50_000, 999.99m));
        await ProcessExpectingAcceptanceAsync(TestOrders.NewIncomingOrder("VALE3", orderSide, 1, 0.01m));

        var exposureBeforeRejection = await postgres.ReadExposureOfSymbolAsync("VALE3");
        Assert.Equal(limitSign * (ExposureLimit.PerSymbol - 999.99m), exposureBeforeRejection);

        var orderOneCentPastTheLimit = await postgres.OrderProcessor.ProcessIncomingOrderAsync(
            TestOrders.NewIncomingOrder("VALE3", orderSide, 10, 100.00m));

        Assert.False(orderOneCentPastTheLimit.Accepted);
        Assert.Equal(ExposureLimit.RejectionText("VALE3"), orderOneCentPastTheLimit.RejectReason);
        Assert.Equal(exposureBeforeRejection, await postgres.ReadExposureOfSymbolAsync("VALE3"));

        await ProcessExpectingAcceptanceAsync(TestOrders.NewIncomingOrder("VALE3", orderSide, 1, 999.99m));
        Assert.Equal(limitSign * ExposureLimit.PerSymbol, await postgres.ReadExposureOfSymbolAsync("VALE3"));
    }

    [Fact]
    public async Task At_the_limit_an_order_in_the_other_direction_is_still_accepted()
    {
        await ProcessExpectingAcceptanceAsync(TestOrders.NewBuyOrder("VIIA4", 50_000, 999.99m));
        await ProcessExpectingAcceptanceAsync(TestOrders.NewBuyOrder("VIIA4", 50_000, 999.99m));
        await ProcessExpectingAcceptanceAsync(TestOrders.NewBuyOrder("VIIA4", 1_000, 1.00m));

        await ProcessExpectingAcceptanceAsync(TestOrders.NewSellOrder("VIIA4", 10, 100.00m));

        Assert.Equal(ExposureLimit.PerSymbol - 1_000m, await postgres.ReadExposureOfSymbolAsync("VIIA4"));
    }

    private async Task ProcessExpectingAcceptanceAsync(IncomingOrder incomingOrder)
    {
        var orderOutcome = await postgres.OrderProcessor.ProcessIncomingOrderAsync(incomingOrder);
        Assert.True(orderOutcome.Accepted,
            $"ordem {incomingOrder.Symbol} {incomingOrder.Side} {incomingOrder.Quantity} x {incomingOrder.Price} foi rejeitada: {orderOutcome.RejectReason}");
    }
}
