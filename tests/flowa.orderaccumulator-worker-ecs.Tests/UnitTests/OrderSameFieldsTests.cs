using Flowa.OrderAccumulator.Domain.Orders.Entities;
using Flowa.OrderAccumulator.Domain.Orders.ValueObjects;

namespace Flowa.OrderAccumulator.Tests;

public sealed class OrderSameFieldsTests
{
    private static readonly Order StoredPetr4BuyOrder = Order.AcceptOrder(new IncomingOrder("stored-order", "PETR4", '1', 100, 10.50m));

    [Fact]
    public void Order_received_again_with_equal_symbol_side_quantity_and_price_has_the_same_fields()
    {
        // Arrange
        var repeatedIncomingOrder = new IncomingOrder("stored-order", "PETR4", '1', 100.0m, 10.5m);

        // Act
        var hasTheSameOrderFields = StoredPetr4BuyOrder.HasTheSameOrderFieldsAs(repeatedIncomingOrder);

        // Assert
        Assert.True(hasTheSameOrderFields);
    }

    [Theory]
    [InlineData("VALE3", '1', 100, "10.50")]
    [InlineData("petr4", '1', 100, "10.50")]
    [InlineData("PETR4", '2', 100, "10.50")]
    [InlineData("PETR4", '1', 101, "10.50")]
    [InlineData("PETR4", '1', 100, "10.51")]
    public void Order_received_again_with_one_other_field_does_not_have_the_same_fields(string incomingSymbol, char incomingSide, int incomingQuantity, string incomingPrice)
    {
        // Arrange
        var duplicateIncomingOrder = new IncomingOrder(
            "stored-order", incomingSymbol, incomingSide, incomingQuantity, decimal.Parse(incomingPrice, System.Globalization.CultureInfo.InvariantCulture));

        // Act
        var hasTheSameOrderFields = StoredPetr4BuyOrder.HasTheSameOrderFieldsAs(duplicateIncomingOrder);

        // Assert
        Assert.False(hasTheSameOrderFields);
    }

    [Fact]
    public void Duplicate_cl_ord_id_rejection_text_names_the_cl_ord_id()
    {
        // Act
        var duplicateClOrdIdRejectionText = Order.BuildDuplicateClOrdIdRejectionText("abc-123");

        // Assert
        Assert.Equal("Ordem rejeitada: o ClOrdID abc-123 já foi usado com outros dados.", duplicateClOrdIdRejectionText);
    }
}
