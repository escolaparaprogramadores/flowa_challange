using Base.OrderAccumulator.Domain.Orders;

namespace Base.OrderAccumulator.Tests;

// Edge tests of the field rule, moved from the old shared project; the limits are the ones of today (CA-27).
public class OrderFieldRuleTests
{
    private const string ValidOrderSymbol = "PETR4";
    private const char BuyOrderSide = '1';
    private const decimal ValidOrderQuantity = 100m;
    private const decimal ValidOrderPrice = 10.50m;

    private static OrderFieldValidation ValidateOrderFields(string? orderSymbol, char orderSide, decimal orderQuantity, decimal orderPrice) =>
        OrderFieldRule.ValidateIncomingOrderFields(new IncomingOrder("ord-1", orderSymbol, orderSide, orderQuantity, orderPrice));

    private static void AssertOrderRejectedWithSingleReason(OrderFieldValidation orderFieldValidation, string expectedRejectionReason)
    {
        Assert.Null(orderFieldValidation.ValidOrderFields);
        Assert.Equal(expectedRejectionReason, Assert.Single(orderFieldValidation.InvalidOrderFieldMessages));
    }

    [Fact]
    public void Allowed_symbols_are_only_the_three_of_the_challenge()
    {
        Assert.Equal(["PETR4", "VALE3", "VIIA4"], OrderFieldRule.AllowedOrderSymbols);
    }

    [Theory]
    [InlineData("PETR4")]
    [InlineData("VALE3")]
    [InlineData("VIIA4")]
    public void Allowed_symbol_is_accepted(string orderSymbol)
    {
        var orderFieldValidation = ValidateOrderFields(orderSymbol, BuyOrderSide, ValidOrderQuantity, ValidOrderPrice);

        Assert.Equal(orderSymbol, orderFieldValidation.ValidOrderFields!.Symbol);
        Assert.Empty(orderFieldValidation.InvalidOrderFieldMessages);
    }

    [Theory]
    [InlineData("petr4", "Símbolo inválido. Use PETR4, VALE3 ou VIIA4.")]
    [InlineData("ABCD3", "Símbolo inválido. Use PETR4, VALE3 ou VIIA4.")]
    [InlineData("ITUB4", "Símbolo inválido. Use PETR4, VALE3 ou VIIA4.")]
    [InlineData("PETR4 ", "Símbolo inválido. Use PETR4, VALE3 ou VIIA4.")]
    [InlineData("", "Informe o símbolo.")]
    [InlineData(null, "Informe o símbolo.")]
    public void Symbol_outside_the_list_or_empty_is_rejected(string? orderSymbol, string expectedRejectionReason)
    {
        AssertOrderRejectedWithSingleReason(ValidateOrderFields(orderSymbol, BuyOrderSide, ValidOrderQuantity, ValidOrderPrice), expectedRejectionReason);
    }

    [Theory]
    [InlineData('1', OrderSide.Buy)]
    [InlineData('2', OrderSide.Sell)]
    public void Buy_and_sell_fix_sides_are_accepted(char orderSide, OrderSide expectedOrderSide)
    {
        Assert.Equal(expectedOrderSide, ValidateOrderFields(ValidOrderSymbol, orderSide, ValidOrderQuantity, ValidOrderPrice).ValidOrderFields!.Side);
    }

    [Theory]
    [InlineData('0')]
    [InlineData('3')]
    [InlineData('5')]
    public void Fix_side_other_than_buy_or_sell_is_rejected(char orderSide)
    {
        AssertOrderRejectedWithSingleReason(
            ValidateOrderFields(ValidOrderSymbol, orderSide, ValidOrderQuantity, ValidOrderPrice), "Lado inválido. Use compra ou venda.");
    }

    public static TheoryData<decimal, int> AcceptedQuantities => new() { { 1m, 1 }, { 99999m, 99999 }, { 100.0m, 100 } };

    [Theory]
    [MemberData(nameof(AcceptedQuantities))]
    public void Quantity_on_the_edges_is_accepted(decimal orderQuantity, int expectedOrderQuantity)
    {
        Assert.Equal(expectedOrderQuantity, ValidateOrderFields(ValidOrderSymbol, BuyOrderSide, orderQuantity, ValidOrderPrice).ValidOrderFields!.Quantity);
    }

    public static TheoryData<decimal, string> RejectedQuantities => new()
    {
        { 0m, "A quantidade deve ser maior que zero." },
        { -1m, "A quantidade deve ser maior que zero." },
        { 1.5m, "A quantidade deve ser um número inteiro." },
        { 99999.0000000000000000000001m, "A quantidade deve ser um número inteiro." },
        { 100000m, "A quantidade deve ser menor que 100.000." },
        { decimal.MaxValue, "A quantidade deve ser menor que 100.000." }
    };

    [Theory]
    [MemberData(nameof(RejectedQuantities))]
    public void Quantity_outside_the_rule_is_rejected(decimal orderQuantity, string expectedRejectionReason)
    {
        AssertOrderRejectedWithSingleReason(ValidateOrderFields(ValidOrderSymbol, BuyOrderSide, orderQuantity, ValidOrderPrice), expectedRejectionReason);
    }

    // 10.500m proves that a zero after the cent is not an extra decimal place.
    public static TheoryData<decimal> AcceptedPrices => new() { 0.01m, 999.99m, 10.500m };

    [Theory]
    [MemberData(nameof(AcceptedPrices))]
    public void Price_on_the_edges_is_accepted(decimal orderPrice)
    {
        Assert.Equal(orderPrice, ValidateOrderFields(ValidOrderSymbol, BuyOrderSide, ValidOrderQuantity, orderPrice).ValidOrderFields!.Price);
    }

    public static TheoryData<decimal, string> RejectedPrices => new()
    {
        { 0m, "O preço deve ser maior que zero." },
        { -0.01m, "O preço deve ser maior que zero." },
        { 1000m, "O preço deve ser menor que 1.000,00." },
        { 10.005m, "O preço deve ser múltiplo de 0,01." },
        { 999.999m, "O preço deve ser múltiplo de 0,01." }
    };

    [Theory]
    [MemberData(nameof(RejectedPrices))]
    public void Price_outside_the_rule_is_rejected(decimal orderPrice, string expectedRejectionReason)
    {
        AssertOrderRejectedWithSingleReason(ValidateOrderFields(ValidOrderSymbol, BuyOrderSide, ValidOrderQuantity, orderPrice), expectedRejectionReason);
    }

    [Fact]
    public void Valid_order_comes_back_with_the_typed_fields()
    {
        var orderFieldValidation = ValidateOrderFields("VIIA4", '2', 250m, 35.10m);

        Assert.Empty(orderFieldValidation.InvalidOrderFieldMessages);
        var validOrderFields = orderFieldValidation.ValidOrderFields!;
        Assert.Equal(("VIIA4", OrderSide.Sell, 250, 35.10m), (validOrderFields.Symbol, validOrderFields.Side, validOrderFields.Quantity, validOrderFields.Price));
    }

    // CA-28: the fields of the acceptance test plus an unknown side, one reason each, in the order symbol, side, quantity, price.
    [Fact]
    public void Every_invalid_field_gets_one_reason_in_the_contract_order()
    {
        var orderFieldValidation = ValidateOrderFields("ITUB4", '9', 100000m, 1000m);

        Assert.Null(orderFieldValidation.ValidOrderFields);
        Assert.Equal(
            [
                "Símbolo inválido. Use PETR4, VALE3 ou VIIA4.",
                "Lado inválido. Use compra ou venda.",
                "A quantidade deve ser menor que 100.000.",
                "O preço deve ser menor que 1.000,00."
            ],
            orderFieldValidation.InvalidOrderFieldMessages);
    }
}
