using Base.OrderGenerator.Domain.Orders.Enums;
using Base.OrderGenerator.Domain.Orders.ValueObjects;

namespace Base.OrderGenerator.Tests;

// Decision 24: the OrderGenerator checks only what a NewOrderSingle cannot carry. Limits, symbols,
// whole quantity and the 0.01 step are left to the OrderAccumulator (decision 12).
public class OrderToSendFormatValidationTests
{
    private const string ValidOrderSymbol = "PETR4";
    private const string ValidOrderSideText = "buy";
    private const string ValidOrderQuantityText = "100";
    private const string ValidOrderPriceText = "10.50";

    private static void AssertOrderRefusedWithSingleFormatError(
        OrderFormatValidation orderFormatValidation, string expectedOrderField, string expectedOrderFieldFormatMessage)
    {
        Assert.Null(orderFormatValidation.OrderToSend);
        Assert.Equal(new OrderFieldFormatError(expectedOrderField, expectedOrderFieldFormatMessage), Assert.Single(orderFormatValidation.OrderFieldFormatErrors));
    }

    // These break the field rule but fit FIX: they must go out as typed, for the OrderAccumulator to judge.
    [Theory]
    [InlineData("ITUB4", "buy", "100000", "1000", "ITUB4", OrderSide.Buy, 100000, 1000)]
    [InlineData("petr4", "sell", "0", "0", "petr4", OrderSide.Sell, 0, 0)]
    [InlineData("PETR4", "buy", "-1", "-0.01", "PETR4", OrderSide.Buy, -1, -0.01)]
    [InlineData("PETR4", "buy", "1.5", "10.005", "PETR4", OrderSide.Buy, 1.5, 10.005)]
    [InlineData("PETR4 ", "sell", "+5", "999.99", "PETR4 ", OrderSide.Sell, 5, 999.99)]
    public void Order_that_fits_fix_goes_out_as_typed_even_when_the_rule_would_reject_it(
        string orderSymbol, string orderSide, string orderQuantity, string orderPrice,
        string expectedSymbol, OrderSide expectedSide, double expectedQuantity, double expectedPrice)
    {
        var orderFormatValidation = OrderToSend.ValidateOrderFormat(orderSymbol, orderSide, orderQuantity, orderPrice);

        Assert.Empty(orderFormatValidation.OrderFieldFormatErrors);
        Assert.Equal(new OrderToSend(expectedSymbol, expectedSide, (decimal)expectedQuantity, (decimal)expectedPrice), orderFormatValidation.OrderToSend);
    }

    [Fact]
    public void Price_keeps_the_decimal_places_that_were_typed()
    {
        var orderToSend = OrderToSend.ValidateOrderFormat(ValidOrderSymbol, ValidOrderSideText, "100.0", "10.500").OrderToSend!;

        Assert.Equal("100.0", orderToSend.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("10.500", orderToSend.Price.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("", "Informe o símbolo.")]
    [InlineData(null, "Informe o símbolo.")]
    [InlineData("PE\u0001TR4", "O símbolo não pode ter caractere de controle.")]
    [InlineData("PETR4\n", "O símbolo não pode ter caractere de controle.")]
    public void Symbol_missing_or_with_control_character_is_refused(string? orderSymbol, string expectedOrderFieldFormatMessage)
    {
        AssertOrderRefusedWithSingleFormatError(
            OrderToSend.ValidateOrderFormat(orderSymbol, ValidOrderSideText, ValidOrderQuantityText, ValidOrderPriceText),
            "symbol", expectedOrderFieldFormatMessage);
    }

    [Theory]
    [InlineData("buy", OrderSide.Buy)]
    [InlineData("sell", OrderSide.Sell)]
    public void Buy_and_sell_are_the_only_sides(string orderSide, OrderSide expectedOrderSide)
    {
        var orderToSend = OrderToSend.ValidateOrderFormat(ValidOrderSymbol, orderSide, ValidOrderQuantityText, ValidOrderPriceText).OrderToSend!;

        Assert.Equal(expectedOrderSide, orderToSend.Side);
    }

    [Theory]
    [InlineData("BUY", "Lado inválido. Use compra ou venda.")]
    [InlineData("compra", "Lado inválido. Use compra ou venda.")]
    [InlineData("1", "Lado inválido. Use compra ou venda.")]
    [InlineData("short", "Lado inválido. Use compra ou venda.")]
    [InlineData("", "Informe o lado da ordem.")]
    [InlineData(null, "Informe o lado da ordem.")]
    public void Unknown_or_missing_side_is_refused(string? orderSide, string expectedOrderFieldFormatMessage)
    {
        AssertOrderRefusedWithSingleFormatError(
            OrderToSend.ValidateOrderFormat(ValidOrderSymbol, orderSide, ValidOrderQuantityText, ValidOrderPriceText),
            "side", expectedOrderFieldFormatMessage);
    }

    // 32 digits overflow decimal; 32 significant digits after the point would be rounded by decimal.
    [Theory]
    [InlineData("abc", "A quantidade deve ser um número inteiro.")]
    [InlineData(" 100", "A quantidade deve ser um número inteiro.")]
    [InlineData("100 ", "A quantidade deve ser um número inteiro.")]
    [InlineData("1e3", "A quantidade deve ser um número inteiro.")]
    [InlineData("1,5", "A quantidade deve ser um número inteiro.")]
    [InlineData("99999999999999999999999999999999", "A quantidade deve ser um número inteiro.")]
    [InlineData("99999.00000000000000000000000001", "A quantidade deve ser um número inteiro.")]
    [InlineData("", "Informe a quantidade.")]
    [InlineData(null, "Informe a quantidade.")]
    public void Quantity_that_is_not_a_fix_number_is_refused(string? orderQuantity, string expectedOrderFieldFormatMessage)
    {
        AssertOrderRefusedWithSingleFormatError(
            OrderToSend.ValidateOrderFormat(ValidOrderSymbol, ValidOrderSideText, orderQuantity, ValidOrderPriceText),
            "quantity", expectedOrderFieldFormatMessage);
    }

    [Theory]
    [InlineData("abc", "O preço deve ser um número.")]
    [InlineData("10,005", "O preço deve ser um número.")]
    [InlineData(" 10.50", "O preço deve ser um número.")]
    [InlineData("1e2", "O preço deve ser um número.")]
    [InlineData("-99999999999999999999999999999999", "O preço deve ser um número.")]
    [InlineData("10.0000000000000000000000000001", "O preço deve ser um número.")]
    [InlineData("", "Informe o preço.")]
    [InlineData(null, "Informe o preço.")]
    public void Price_that_is_not_a_fix_number_is_refused(string? orderPrice, string expectedOrderFieldFormatMessage)
    {
        AssertOrderRefusedWithSingleFormatError(
            OrderToSend.ValidateOrderFormat(ValidOrderSymbol, ValidOrderSideText, ValidOrderQuantityText, orderPrice),
            "price", expectedOrderFieldFormatMessage);
    }

    [Fact]
    public void Every_field_out_of_format_gets_one_error_in_the_contract_order()
    {
        var orderFormatValidation = OrderToSend.ValidateOrderFormat(null, "hold", "abc", null);

        Assert.Null(orderFormatValidation.OrderToSend);
        Assert.Equal(
            [
                new OrderFieldFormatError("symbol", "Informe o símbolo."),
                new OrderFieldFormatError("side", "Lado inválido. Use compra ou venda."),
                new OrderFieldFormatError("quantity", "A quantidade deve ser um número inteiro."),
                new OrderFieldFormatError("price", "Informe o preço.")
            ],
            orderFormatValidation.OrderFieldFormatErrors);
    }
}
