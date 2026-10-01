using Flowa.Shared;

namespace Flowa.Shared.Tests;

public class OrderValidatorTests
{
    // Ordem válida de base; cada teste troca só o campo que está provando.
    private const string ValidOrderSymbol = "PETR4";
    private const string ValidOrderSideText = "buy";
    private const string ValidOrderQuantityText = "100";
    private const string ValidOrderPriceText = "10.50";

    private static void AssertOrderRejectedWithSingleFieldError(OrderValidationResult orderValidationResult, string orderField, string orderFieldErrorMessage)
    {
        Assert.False(orderValidationResult.IsValid);
        Assert.Null(orderValidationResult.Order);
        Assert.Equal(new OrderFieldError(orderField, orderFieldErrorMessage), Assert.Single(orderValidationResult.Errors));
    }

    // Regra: símbolo

    [Theory]
    [InlineData("PETR4")]
    [InlineData("VALE3")]
    [InlineData("VIIA4")]
    public void DeveAceitarSimboloPermitido(string orderSymbol)
    {
        var orderValidationResult = OrderValidator.ValidateOrderFromJson(orderSymbol, ValidOrderSideText, ValidOrderQuantityText, ValidOrderPriceText);

        Assert.True(orderValidationResult.IsValid);
        Assert.Equal(orderSymbol, orderValidationResult.Order!.Symbol);
    }

    [Theory]
    [InlineData("petr4", OrderMessages.SymbolInvalid)]
    [InlineData("ABCD3", OrderMessages.SymbolInvalid)]
    [InlineData("PETR4 ", OrderMessages.SymbolInvalid)]
    [InlineData("", OrderMessages.SymbolRequired)]
    [InlineData(null, OrderMessages.SymbolRequired)]
    public void DeveRecusarSimboloForaDaListaOuVazio(string? orderSymbol, string expectedOrderFieldErrorMessage)
    {
        var orderValidationResult = OrderValidator.ValidateOrderFromJson(orderSymbol, ValidOrderSideText, ValidOrderQuantityText, ValidOrderPriceText);

        AssertOrderRejectedWithSingleFieldError(orderValidationResult, OrderFields.Symbol, expectedOrderFieldErrorMessage);
    }

    // Regra: lado

    [Theory]
    [InlineData("buy", OrderSide.Buy, '1')]
    [InlineData("sell", OrderSide.Sell, '2')]
    public void DeveConverterLadoDaTelaParaFix(string orderSide, OrderSide expectedOrderSide, char expectedOrderSideFixCode)
    {
        var orderValidationResult = OrderValidator.ValidateOrderFromJson(ValidOrderSymbol, orderSide, ValidOrderQuantityText, ValidOrderPriceText);

        Assert.True(orderValidationResult.IsValid);
        Assert.Equal(expectedOrderSide, orderValidationResult.Order!.Side);
        Assert.Equal(expectedOrderSideFixCode, orderValidationResult.Order.Side.ToFixOrderSide());
        Assert.Equal(orderSide, orderValidationResult.Order.Side.ToJsonOrderSide());
    }

    [Theory]
    [InlineData('1', OrderSide.Buy)]
    [InlineData('2', OrderSide.Sell)]
    public void DeveAceitarLadoFix(char orderSide, OrderSide expectedOrderSide)
    {
        var orderValidationResult = OrderValidator.ValidateOrderFromFix(ValidOrderSymbol, orderSide, 100m, 10.50m);

        Assert.True(orderValidationResult.IsValid);
        Assert.Equal(expectedOrderSide, orderValidationResult.Order!.Side);
    }

    [Theory]
    [InlineData("BUY", OrderMessages.SideInvalid)]
    [InlineData("compra", OrderMessages.SideInvalid)]
    [InlineData("1", OrderMessages.SideInvalid)]
    [InlineData("short", OrderMessages.SideInvalid)]
    [InlineData("", OrderMessages.SideRequired)]
    [InlineData(null, OrderMessages.SideRequired)]
    public void DeveRecusarLadoDesconhecidoOuVazio(string? orderSide, string expectedOrderFieldErrorMessage)
    {
        var orderValidationResult = OrderValidator.ValidateOrderFromJson(ValidOrderSymbol, orderSide, ValidOrderQuantityText, ValidOrderPriceText);

        AssertOrderRejectedWithSingleFieldError(orderValidationResult, OrderFields.Side, expectedOrderFieldErrorMessage);
    }

    [Theory]
    [InlineData('0')]
    [InlineData('3')]
    [InlineData('5')]
    public void DeveRecusarLadoFixDiferenteDeCompraEVenda(char orderSide)
    {
        var orderValidationResult = OrderValidator.ValidateOrderFromFix(ValidOrderSymbol, orderSide, 100m, 10.50m);

        AssertOrderRejectedWithSingleFieldError(orderValidationResult, OrderFields.Side, OrderMessages.SideInvalid);
    }

    // Regra: quantidade

    // "100.0" e "+5" têm valor inteiro: o número JSON 100.0 é o mesmo que 100.
    [Theory]
    [InlineData("1", 1)]
    [InlineData("99999", 99999)]
    [InlineData("100.0", 100)]
    [InlineData("+5", 5)]
    public void DeveAceitarQuantidadeNasBordas(string orderQuantity, int expectedOrderQuantity)
    {
        var orderValidationResult = OrderValidator.ValidateOrderFromJson(ValidOrderSymbol, ValidOrderSideText, orderQuantity, ValidOrderPriceText);

        Assert.True(orderValidationResult.IsValid);
        Assert.Equal(expectedOrderQuantity, orderValidationResult.Order!.Quantity);
    }

    [Theory]
    [InlineData("0", OrderMessages.QuantityNotPositive)]
    [InlineData("-1", OrderMessages.QuantityNotPositive)]
    [InlineData("1.5", OrderMessages.QuantityNotInteger)]
    [InlineData("abc", OrderMessages.QuantityNotInteger)]
    [InlineData("100000", OrderMessages.QuantityTooLarge)]
    [InlineData("99999999999999999999999999999999", OrderMessages.QuantityTooLarge)]
    [InlineData("-99999999999999999999999999999999", OrderMessages.QuantityNotPositive)]
    [InlineData(" 100", OrderMessages.QuantityNotInteger)]
    [InlineData("100 ", OrderMessages.QuantityNotInteger)]
    [InlineData("1e3", OrderMessages.QuantityNotInteger)]
    [InlineData("99999.00000000000000000000000001", OrderMessages.QuantityNotInteger)]
    [InlineData("", OrderMessages.QuantityRequired)]
    [InlineData(null, OrderMessages.QuantityRequired)]
    public void DeveRecusarQuantidadeInvalida(string? orderQuantity, string expectedOrderFieldErrorMessage)
    {
        var orderValidationResult = OrderValidator.ValidateOrderFromJson(ValidOrderSymbol, ValidOrderSideText, orderQuantity, ValidOrderPriceText);

        AssertOrderRejectedWithSingleFieldError(orderValidationResult, OrderFields.Quantity, expectedOrderFieldErrorMessage);
    }

    public static TheoryData<decimal, int> QuantidadesFixAceitas => new() { { 1m, 1 }, { 99999m, 99999 } };

    [Theory]
    [MemberData(nameof(QuantidadesFixAceitas))]
    public void DeveAceitarQuantidadeFixNasBordas(decimal orderQuantity, int expectedOrderQuantity)
    {
        var orderValidationResult = OrderValidator.ValidateOrderFromFix(ValidOrderSymbol, OrderSideCodes.BuyFix, orderQuantity, 10.50m);

        Assert.True(orderValidationResult.IsValid);
        Assert.Equal(expectedOrderQuantity, orderValidationResult.Order!.Quantity);
    }

    public static TheoryData<decimal, string> QuantidadesFixRecusadas => new()
    {
        { 0m, OrderMessages.QuantityNotPositive },
        { -1m, OrderMessages.QuantityNotPositive },
        { 1.5m, OrderMessages.QuantityNotInteger },
        { 100000m, OrderMessages.QuantityTooLarge }
    };

    [Theory]
    [MemberData(nameof(QuantidadesFixRecusadas))]
    public void DeveRecusarQuantidadeFixInvalida(decimal orderQuantity, string expectedOrderFieldErrorMessage)
    {
        var orderValidationResult = OrderValidator.ValidateOrderFromFix(ValidOrderSymbol, OrderSideCodes.BuyFix, orderQuantity, 10.50m);

        AssertOrderRejectedWithSingleFieldError(orderValidationResult, OrderFields.Quantity, expectedOrderFieldErrorMessage);
    }

    // Regra: preço

    // "10.500" prova que zero à direita do centavo não conta como casa a mais.
    public static TheoryData<string, decimal> PrecosAceitos => new() { { "0.01", 0.01m }, { "999.99", 999.99m }, { "10.500", 10.50m } };

    [Theory]
    [MemberData(nameof(PrecosAceitos))]
    public void DeveAceitarPrecoNasBordasNasDuasEntradas(string orderPriceText, decimal orderPrice)
    {
        var jsonOrderValidationResult = OrderValidator.ValidateOrderFromJson(ValidOrderSymbol, ValidOrderSideText, ValidOrderQuantityText, orderPriceText);
        var fixOrderValidationResult = OrderValidator.ValidateOrderFromFix(ValidOrderSymbol, OrderSideCodes.BuyFix, 100m, orderPrice);

        Assert.True(jsonOrderValidationResult.IsValid);
        Assert.Equal(orderPrice, jsonOrderValidationResult.Order!.Price);
        Assert.True(fixOrderValidationResult.IsValid);
        Assert.Equal(orderPrice, fixOrderValidationResult.Order!.Price);
    }

    [Theory]
    [InlineData("0", OrderMessages.PriceNotPositive)]
    [InlineData("-0.01", OrderMessages.PriceNotPositive)]
    [InlineData("1000", OrderMessages.PriceTooLarge)]
    [InlineData("10.005", OrderMessages.PriceOffTick)]
    [InlineData("10.0000000000000000000000000001", OrderMessages.PriceOffTick)]
    [InlineData("abc", OrderMessages.PriceNotNumber)]
    [InlineData("10,005", OrderMessages.PriceNotNumber)]
    [InlineData(" 10.50", OrderMessages.PriceNotNumber)]
    [InlineData("99999999999999999999999999999999", OrderMessages.PriceTooLarge)]
    [InlineData("-99999999999999999999999999999999", OrderMessages.PriceNotPositive)]
    [InlineData("", OrderMessages.PriceRequired)]
    [InlineData(null, OrderMessages.PriceRequired)]
    public void DeveRecusarPrecoInvalido(string? orderPrice, string expectedOrderFieldErrorMessage)
    {
        var orderValidationResult = OrderValidator.ValidateOrderFromJson(ValidOrderSymbol, ValidOrderSideText, ValidOrderQuantityText, orderPrice);

        AssertOrderRejectedWithSingleFieldError(orderValidationResult, OrderFields.Price, expectedOrderFieldErrorMessage);
    }

    public static TheoryData<decimal, string> PrecosFixRecusados => new()
    {
        { 0m, OrderMessages.PriceNotPositive },
        { -0.01m, OrderMessages.PriceNotPositive },
        { 1000m, OrderMessages.PriceTooLarge },
        { 10.005m, OrderMessages.PriceOffTick }
    };

    [Theory]
    [MemberData(nameof(PrecosFixRecusados))]
    public void DeveRecusarPrecoFixInvalido(decimal orderPrice, string expectedOrderFieldErrorMessage)
    {
        var orderValidationResult = OrderValidator.ValidateOrderFromFix(ValidOrderSymbol, OrderSideCodes.BuyFix, 100m, orderPrice);

        AssertOrderRejectedWithSingleFieldError(orderValidationResult, OrderFields.Price, expectedOrderFieldErrorMessage);
    }

    // Ordem inteira

    [Fact]
    public void DeveDevolverOrdemTipadaQuandoTudoEstaValido()
    {
        var orderValidationResult = OrderValidator.ValidateOrderFromJson("VALE3", "sell", "250", "35.10");

        Assert.True(orderValidationResult.IsValid);
        Assert.Empty(orderValidationResult.Errors);
        Assert.Equal(new ValidOrder("VALE3", OrderSide.Sell, 250, 35.10m), orderValidationResult.Order);
    }

    [Fact]
    public void DeveListarUmErroPorCampoQuandoTudoEstaInvalido()
    {
        var orderValidationResult = OrderValidator.ValidateOrderFromJson("XPTO", "hold", "0", "1000");

        Assert.Null(orderValidationResult.Order);
        Assert.Equal(
            [
                new OrderFieldError(OrderFields.Symbol, OrderMessages.SymbolInvalid),
                new OrderFieldError(OrderFields.Side, OrderMessages.SideInvalid),
                new OrderFieldError(OrderFields.Quantity, OrderMessages.QuantityNotPositive),
                new OrderFieldError(OrderFields.Price, OrderMessages.PriceTooLarge)
            ],
            orderValidationResult.Errors);
    }

    [Fact]
    public void DeveDevolverOrdemTipadaQuandoTudoEstaValidoNoFix()
    {
        var orderValidationResult = OrderValidator.ValidateOrderFromFix("VIIA4", OrderSideCodes.SellFix, 250m, 35.10m);

        Assert.True(orderValidationResult.IsValid);
        Assert.Empty(orderValidationResult.Errors);
        Assert.Equal(new ValidOrder("VIIA4", OrderSide.Sell, 250, 35.10m), orderValidationResult.Order);
    }

    [Fact]
    public void DeveListarUmErroPorCampoQuandoTudoEstaInvalidoNoFix()
    {
        var orderValidationResult = OrderValidator.ValidateOrderFromFix("XPTO", '9', 1.5m, 10.005m);

        Assert.Null(orderValidationResult.Order);
        Assert.Equal(
            [
                new OrderFieldError(OrderFields.Symbol, OrderMessages.SymbolInvalid),
                new OrderFieldError(OrderFields.Side, OrderMessages.SideInvalid),
                new OrderFieldError(OrderFields.Quantity, OrderMessages.QuantityNotInteger),
                new OrderFieldError(OrderFields.Price, OrderMessages.PriceOffTick)
            ],
            orderValidationResult.Errors);
    }

    [Fact]
    public void DeveTerSoOsTresSimbolosDoEnunciado()
    {
        Assert.Equal(["PETR4", "VALE3", "VIIA4"], OrderRules.Symbols);
    }
}
