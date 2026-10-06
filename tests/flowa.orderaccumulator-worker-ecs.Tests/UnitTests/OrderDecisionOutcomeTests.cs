using Flowa.OrderAccumulator.Domain.Orders.Entities;
using Flowa.OrderAccumulator.Domain.Orders.Enums;
using Flowa.OrderAccumulator.Domain.Orders.ValueObjects;

namespace Flowa.OrderAccumulator.Tests;

public sealed class OrderDecisionOutcomeTests
{
    private static readonly IncomingOrder ValidBuyOrder = new("cl-ord-valid", "PETR4", OrderSideCodes.BuyOrderSideFixCode, 100, 10.50m);

    [Fact]
    public void Accepted_order_is_classified_as_accepted()
    {
        Assert.Equal(OrderDecisionOutcome.Accepted, Order.AcceptOrder(ValidBuyOrder).ClassifyOrderDecision());
    }

    [Fact]
    public void Order_rejected_by_the_field_rule_is_classified_as_rejected_for_invalid_fields()
    {
        var orderWithInvalidSymbol = new IncomingOrder("cl-ord-invalid", "XXXX3", OrderSideCodes.BuyOrderSideFixCode, 100, 10.50m);

        var rejectedOrder = Order.RejectOrderWithInvalidFields(orderWithInvalidSymbol, [OrderFieldMessages.OrderSymbolInvalidMessage]);

        Assert.Equal(OrderDecisionOutcome.RejectedForInvalidFields, rejectedOrder.ClassifyOrderDecision());
    }

    [Fact]
    public void Order_rejected_by_the_limit_is_classified_as_rejected_over_exposure_limit()
    {
        Assert.Equal(OrderDecisionOutcome.RejectedOverExposureLimit, Order.RejectOrderOverExposureLimit(ValidBuyOrder, "PETR4").ClassifyOrderDecision());
    }

    [Theory]
    [InlineData("PETR4", '1', 100, 10.50, OrderDecisionOutcome.RejectedOverExposureLimit)]
    [InlineData("PETR4", '9', 100, 10.50, OrderDecisionOutcome.RejectedForInvalidFields)]
    [InlineData(null, '1', 100, 10.50, OrderDecisionOutcome.RejectedForInvalidFields)]
    public void Rejected_order_loaded_from_the_database_keeps_its_classification(
        string? storedSymbol, char storedSide, int storedQuantity, double storedPrice, OrderDecisionOutcome expectedOutcome)
    {
        var restoredOrder = Order.RestoreOrder(
            "cl-ord-stored", Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), storedSymbol, storedSide, storedQuantity, (decimal)storedPrice,
            accepted: false, rejectReason: "Motivo gravado.");

        Assert.Equal(expectedOutcome, restoredOrder.ClassifyOrderDecision());
    }
}
