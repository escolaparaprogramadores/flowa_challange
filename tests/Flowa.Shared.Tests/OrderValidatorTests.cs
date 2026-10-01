using Flowa.Shared;

namespace Flowa.Shared.Tests;

public class OrderValidatorTests
{
    // Ordem válida de base; cada teste troca só o campo que está provando.
    private const string ValidSymbol = "PETR4";
    private const string ValidSide = "buy";
    private const string ValidQuantity = "100";
    private const string ValidPrice = "10.50";

    private static void AssertSingleError(OrderValidationResult result, string field, string message)
    {
        Assert.False(result.IsValid);
        Assert.Null(result.Order);
        Assert.Equal(new FieldError(field, message), Assert.Single(result.Errors));
    }

    // CA-1 — símbolo

    [Theory]
    [InlineData("PETR4")]
    [InlineData("VALE3")]
    [InlineData("VIIA4")]
    public void DeveAceitarSimboloPermitido(string symbol)
    {
        var result = OrderValidator.Validate(symbol, ValidSide, ValidQuantity, ValidPrice);

        Assert.True(result.IsValid);
        Assert.Equal(symbol, result.Order!.Symbol);
    }

    [Theory]
    [InlineData("petr4")]
    [InlineData("ABCD3")]
    [InlineData("PETR4 ")]
    public void DeveRecusarSimboloForaDaLista(string symbol)
    {
        var result = OrderValidator.Validate(symbol, ValidSide, ValidQuantity, ValidPrice);

        AssertSingleError(result, OrderFields.Symbol, OrderMessages.SymbolInvalid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void DeveRecusarSimboloVazio(string? symbol)
    {
        var result = OrderValidator.Validate(symbol, ValidSide, ValidQuantity, ValidPrice);

        AssertSingleError(result, OrderFields.Symbol, OrderMessages.SymbolRequired);
    }

    [Fact]
    public void DeveRecusarSimboloForaDaListaVindoDoFix()
    {
        var result = OrderValidator.Validate("ABCD3", SideCodes.BuyFix, 100m, 10.50m);

        AssertSingleError(result, OrderFields.Symbol, OrderMessages.SymbolInvalid);
    }

    // CA-2 — lado

    [Theory]
    [InlineData("buy", Side.Buy, '1')]
    [InlineData("sell", Side.Sell, '2')]
    public void DeveConverterLadoDaTelaParaFix(string side, Side expectedSide, char expectedFix)
    {
        var result = OrderValidator.Validate(ValidSymbol, side, ValidQuantity, ValidPrice);

        Assert.True(result.IsValid);
        Assert.Equal(expectedSide, result.Order!.Side);
        Assert.Equal(expectedFix, result.Order.Side.ToFix());
        Assert.Equal(side, result.Order.Side.ToJson());
    }

    [Theory]
    [InlineData('1', Side.Buy)]
    [InlineData('2', Side.Sell)]
    public void DeveAceitarLadoFix(char side, Side expectedSide)
    {
        var result = OrderValidator.Validate(ValidSymbol, side, 100m, 10.50m);

        Assert.True(result.IsValid);
        Assert.Equal(expectedSide, result.Order!.Side);
    }

    [Theory]
    [InlineData("BUY")]
    [InlineData("compra")]
    [InlineData("1")]
    [InlineData("short")]
    public void DeveRecusarLadoDesconhecido(string side)
    {
        var result = OrderValidator.Validate(ValidSymbol, side, ValidQuantity, ValidPrice);

        AssertSingleError(result, OrderFields.Side, OrderMessages.SideInvalid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void DeveRecusarLadoVazio(string? side)
    {
        var result = OrderValidator.Validate(ValidSymbol, side, ValidQuantity, ValidPrice);

        AssertSingleError(result, OrderFields.Side, OrderMessages.SideRequired);
    }

    [Theory]
    [InlineData('0')]
    [InlineData('3')]
    [InlineData('5')]
    public void DeveRecusarLadoFixDiferenteDeCompraEVenda(char side)
    {
        var result = OrderValidator.Validate(ValidSymbol, side, 100m, 10.50m);

        AssertSingleError(result, OrderFields.Side, OrderMessages.SideInvalid);
    }

    // CA-3 — quantidade

    // "100.0" e "+5" têm valor inteiro: o número JSON 100.0 é o mesmo que 100.
    [Theory]
    [InlineData("1", 1)]
    [InlineData("99999", 99999)]
    [InlineData("100.0", 100)]
    [InlineData("+5", 5)]
    public void DeveAceitarQuantidadeNasBordas(string quantity, int expected)
    {
        var result = OrderValidator.Validate(ValidSymbol, ValidSide, quantity, ValidPrice);

        Assert.True(result.IsValid);
        Assert.Equal(expected, result.Order!.Quantity);
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
    [InlineData("", OrderMessages.QuantityRequired)]
    [InlineData(null, OrderMessages.QuantityRequired)]
    public void DeveRecusarQuantidadeInvalida(string? quantity, string expectedMessage)
    {
        var result = OrderValidator.Validate(ValidSymbol, ValidSide, quantity, ValidPrice);

        AssertSingleError(result, OrderFields.Quantity, expectedMessage);
    }

    public static TheoryData<decimal, int> QuantidadesFixAceitas => new() { { 1m, 1 }, { 99999m, 99999 } };

    [Theory]
    [MemberData(nameof(QuantidadesFixAceitas))]
    public void DeveAceitarQuantidadeFixNasBordas(decimal quantity, int expected)
    {
        var result = OrderValidator.Validate(ValidSymbol, SideCodes.BuyFix, quantity, 10.50m);

        Assert.True(result.IsValid);
        Assert.Equal(expected, result.Order!.Quantity);
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
    public void DeveRecusarQuantidadeFixInvalida(decimal quantity, string expectedMessage)
    {
        var result = OrderValidator.Validate(ValidSymbol, SideCodes.BuyFix, quantity, 10.50m);

        AssertSingleError(result, OrderFields.Quantity, expectedMessage);
    }

    // CA-4 — preço

    public static TheoryData<string, decimal> PrecosAceitos => new() { { "0.01", 0.01m }, { "999.99", 999.99m } };

    [Theory]
    [MemberData(nameof(PrecosAceitos))]
    public void DeveAceitarPrecoNasBordas(string price, decimal expected)
    {
        var result = OrderValidator.Validate(ValidSymbol, ValidSide, ValidQuantity, price);

        Assert.True(result.IsValid);
        Assert.Equal(expected, result.Order!.Price);
    }

    [Theory]
    [InlineData("0", OrderMessages.PriceNotPositive)]
    [InlineData("-0.01", OrderMessages.PriceNotPositive)]
    [InlineData("1000", OrderMessages.PriceTooLarge)]
    [InlineData("10.005", OrderMessages.PriceOffTick)]
    [InlineData("abc", OrderMessages.PriceNotNumber)]
    [InlineData("10,005", OrderMessages.PriceNotNumber)]
    [InlineData(" 10.50", OrderMessages.PriceNotNumber)]
    [InlineData("99999999999999999999999999999999", OrderMessages.PriceTooLarge)]
    [InlineData("-99999999999999999999999999999999", OrderMessages.PriceNotPositive)]
    [InlineData("", OrderMessages.PriceRequired)]
    [InlineData(null, OrderMessages.PriceRequired)]
    public void DeveRecusarPrecoInvalido(string? price, string expectedMessage)
    {
        var result = OrderValidator.Validate(ValidSymbol, ValidSide, ValidQuantity, price);

        AssertSingleError(result, OrderFields.Price, expectedMessage);
    }

    [Theory]
    [MemberData(nameof(PrecosAceitos))]
    public void DeveAceitarPrecoFixNasBordas(string _, decimal price)
    {
        var result = OrderValidator.Validate(ValidSymbol, SideCodes.BuyFix, 100m, price);

        Assert.True(result.IsValid);
        Assert.Equal(price, result.Order!.Price);
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
    public void DeveRecusarPrecoFixInvalido(decimal price, string expectedMessage)
    {
        var result = OrderValidator.Validate(ValidSymbol, SideCodes.BuyFix, 100m, price);

        AssertSingleError(result, OrderFields.Price, expectedMessage);
    }

    // Ordem inteira

    [Fact]
    public void DeveDevolverOrdemTipadaQuandoTudoEstaValido()
    {
        var result = OrderValidator.Validate("VALE3", "sell", "250", "35.10");

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Equal(new ValidOrder("VALE3", Side.Sell, 250, 35.10m), result.Order);
    }

    [Fact]
    public void DeveListarUmErroPorCampoQuandoTudoEstaInvalido()
    {
        var result = OrderValidator.Validate("XPTO", "hold", "0", "1000");

        Assert.Null(result.Order);
        Assert.Equal(
            [
                new FieldError(OrderFields.Symbol, OrderMessages.SymbolInvalid),
                new FieldError(OrderFields.Side, OrderMessages.SideInvalid),
                new FieldError(OrderFields.Quantity, OrderMessages.QuantityNotPositive),
                new FieldError(OrderFields.Price, OrderMessages.PriceTooLarge)
            ],
            result.Errors);
    }

    [Fact]
    public void DeveDevolverOrdemTipadaQuandoTudoEstaValidoNoFix()
    {
        var result = OrderValidator.Validate("VIIA4", SideCodes.SellFix, 250m, 35.10m);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Equal(new ValidOrder("VIIA4", Side.Sell, 250, 35.10m), result.Order);
    }

    [Fact]
    public void DeveListarUmErroPorCampoQuandoTudoEstaInvalidoNoFix()
    {
        var result = OrderValidator.Validate("XPTO", '9', 1.5m, 10.005m);

        Assert.Null(result.Order);
        Assert.Equal(
            [
                new FieldError(OrderFields.Symbol, OrderMessages.SymbolInvalid),
                new FieldError(OrderFields.Side, OrderMessages.SideInvalid),
                new FieldError(OrderFields.Quantity, OrderMessages.QuantityNotInteger),
                new FieldError(OrderFields.Price, OrderMessages.PriceOffTick)
            ],
            result.Errors);
    }

    [Fact]
    public void DeveTerSoOsTresSimbolosDoEnunciado()
    {
        Assert.Equal(["PETR4", "VALE3", "VIIA4"], OrderRules.Symbols);
    }
}
