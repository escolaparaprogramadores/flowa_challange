using Flowa.Shared;

namespace Flowa.Shared.Tests;

public class OrderValidatorTests
{
    // Ordem válida de base; cada teste troca só o campo que está provando.
    private const string ValidSymbol = "PETR4";
    private const string ValidSide = "buy";
    private const string ValidQuantity = "100";
    private const string ValidPrice = "10.50";

    private static void AssertRejectedWithSingleError(OrderValidationResult validationResult, string field, string message)
    {
        Assert.False(validationResult.IsValid);
        Assert.Null(validationResult.Order);
        Assert.Equal(new FieldError(field, message), Assert.Single(validationResult.Errors));
    }

    // Regra: símbolo

    [Theory]
    [InlineData("PETR4")]
    [InlineData("VALE3")]
    [InlineData("VIIA4")]
    public void DeveAceitarSimboloPermitido(string symbol)
    {
        var validationResult = OrderValidator.ValidateFromJson(symbol, ValidSide, ValidQuantity, ValidPrice);

        Assert.True(validationResult.IsValid);
        Assert.Equal(symbol, validationResult.Order!.Symbol);
    }

    [Theory]
    [InlineData("petr4", OrderMessages.SymbolInvalid)]
    [InlineData("ABCD3", OrderMessages.SymbolInvalid)]
    [InlineData("PETR4 ", OrderMessages.SymbolInvalid)]
    [InlineData("", OrderMessages.SymbolRequired)]
    [InlineData(null, OrderMessages.SymbolRequired)]
    public void DeveRecusarSimboloForaDaListaOuVazio(string? symbol, string expectedMessage)
    {
        var validationResult = OrderValidator.ValidateFromJson(symbol, ValidSide, ValidQuantity, ValidPrice);

        AssertRejectedWithSingleError(validationResult, OrderFields.Symbol, expectedMessage);
    }

    // Regra: lado

    [Theory]
    [InlineData("buy", Side.Buy, '1')]
    [InlineData("sell", Side.Sell, '2')]
    public void DeveConverterLadoDaTelaParaFix(string side, Side expectedSide, char expectedFix)
    {
        var validationResult = OrderValidator.ValidateFromJson(ValidSymbol, side, ValidQuantity, ValidPrice);

        Assert.True(validationResult.IsValid);
        Assert.Equal(expectedSide, validationResult.Order!.Side);
        Assert.Equal(expectedFix, validationResult.Order.Side.ToFix());
        Assert.Equal(side, validationResult.Order.Side.ToJson());
    }

    [Theory]
    [InlineData('1', Side.Buy)]
    [InlineData('2', Side.Sell)]
    public void DeveAceitarLadoFix(char side, Side expectedSide)
    {
        var validationResult = OrderValidator.ValidateFromFix(ValidSymbol, side, 100m, 10.50m);

        Assert.True(validationResult.IsValid);
        Assert.Equal(expectedSide, validationResult.Order!.Side);
    }

    [Theory]
    [InlineData("BUY", OrderMessages.SideInvalid)]
    [InlineData("compra", OrderMessages.SideInvalid)]
    [InlineData("1", OrderMessages.SideInvalid)]
    [InlineData("short", OrderMessages.SideInvalid)]
    [InlineData("", OrderMessages.SideRequired)]
    [InlineData(null, OrderMessages.SideRequired)]
    public void DeveRecusarLadoDesconhecidoOuVazio(string? side, string expectedMessage)
    {
        var validationResult = OrderValidator.ValidateFromJson(ValidSymbol, side, ValidQuantity, ValidPrice);

        AssertRejectedWithSingleError(validationResult, OrderFields.Side, expectedMessage);
    }

    [Theory]
    [InlineData('0')]
    [InlineData('3')]
    [InlineData('5')]
    public void DeveRecusarLadoFixDiferenteDeCompraEVenda(char side)
    {
        var validationResult = OrderValidator.ValidateFromFix(ValidSymbol, side, 100m, 10.50m);

        AssertRejectedWithSingleError(validationResult, OrderFields.Side, OrderMessages.SideInvalid);
    }

    // Regra: quantidade

    // "100.0" e "+5" têm valor inteiro: o número JSON 100.0 é o mesmo que 100.
    [Theory]
    [InlineData("1", 1)]
    [InlineData("99999", 99999)]
    [InlineData("100.0", 100)]
    [InlineData("+5", 5)]
    public void DeveAceitarQuantidadeNasBordas(string quantity, int expected)
    {
        var validationResult = OrderValidator.ValidateFromJson(ValidSymbol, ValidSide, quantity, ValidPrice);

        Assert.True(validationResult.IsValid);
        Assert.Equal(expected, validationResult.Order!.Quantity);
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
    public void DeveRecusarQuantidadeInvalida(string? quantity, string expectedMessage)
    {
        var validationResult = OrderValidator.ValidateFromJson(ValidSymbol, ValidSide, quantity, ValidPrice);

        AssertRejectedWithSingleError(validationResult, OrderFields.Quantity, expectedMessage);
    }

    public static TheoryData<decimal, int> QuantidadesFixAceitas => new() { { 1m, 1 }, { 99999m, 99999 } };

    [Theory]
    [MemberData(nameof(QuantidadesFixAceitas))]
    public void DeveAceitarQuantidadeFixNasBordas(decimal quantity, int expected)
    {
        var validationResult = OrderValidator.ValidateFromFix(ValidSymbol, SideCodes.BuyFix, quantity, 10.50m);

        Assert.True(validationResult.IsValid);
        Assert.Equal(expected, validationResult.Order!.Quantity);
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
        var validationResult = OrderValidator.ValidateFromFix(ValidSymbol, SideCodes.BuyFix, quantity, 10.50m);

        AssertRejectedWithSingleError(validationResult, OrderFields.Quantity, expectedMessage);
    }

    // Regra: preço

    // "10.500" prova que zero à direita do centavo não conta como casa a mais.
    public static TheoryData<string, decimal> PrecosAceitos => new() { { "0.01", 0.01m }, { "999.99", 999.99m }, { "10.500", 10.50m } };

    [Theory]
    [MemberData(nameof(PrecosAceitos))]
    public void DeveAceitarPrecoNasBordas(string price, decimal expected)
    {
        var validationResult = OrderValidator.ValidateFromJson(ValidSymbol, ValidSide, ValidQuantity, price);

        Assert.True(validationResult.IsValid);
        Assert.Equal(expected, validationResult.Order!.Price);
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
    public void DeveRecusarPrecoInvalido(string? price, string expectedMessage)
    {
        var validationResult = OrderValidator.ValidateFromJson(ValidSymbol, ValidSide, ValidQuantity, price);

        AssertRejectedWithSingleError(validationResult, OrderFields.Price, expectedMessage);
    }

    [Theory]
    [MemberData(nameof(PrecosAceitos))]
    public void DeveAceitarPrecoFixNasBordas(string _, decimal price)
    {
        var validationResult = OrderValidator.ValidateFromFix(ValidSymbol, SideCodes.BuyFix, 100m, price);

        Assert.True(validationResult.IsValid);
        Assert.Equal(price, validationResult.Order!.Price);
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
        var validationResult = OrderValidator.ValidateFromFix(ValidSymbol, SideCodes.BuyFix, 100m, price);

        AssertRejectedWithSingleError(validationResult, OrderFields.Price, expectedMessage);
    }

    // Ordem inteira

    [Fact]
    public void DeveDevolverOrdemTipadaQuandoTudoEstaValido()
    {
        var validationResult = OrderValidator.ValidateFromJson("VALE3", "sell", "250", "35.10");

        Assert.True(validationResult.IsValid);
        Assert.Empty(validationResult.Errors);
        Assert.Equal(new ValidOrder("VALE3", Side.Sell, 250, 35.10m), validationResult.Order);
    }

    [Fact]
    public void DeveListarUmErroPorCampoQuandoTudoEstaInvalido()
    {
        var validationResult = OrderValidator.ValidateFromJson("XPTO", "hold", "0", "1000");

        Assert.Null(validationResult.Order);
        Assert.Equal(
            [
                new FieldError(OrderFields.Symbol, OrderMessages.SymbolInvalid),
                new FieldError(OrderFields.Side, OrderMessages.SideInvalid),
                new FieldError(OrderFields.Quantity, OrderMessages.QuantityNotPositive),
                new FieldError(OrderFields.Price, OrderMessages.PriceTooLarge)
            ],
            validationResult.Errors);
    }

    [Fact]
    public void DeveDevolverOrdemTipadaQuandoTudoEstaValidoNoFix()
    {
        var validationResult = OrderValidator.ValidateFromFix("VIIA4", SideCodes.SellFix, 250m, 35.10m);

        Assert.True(validationResult.IsValid);
        Assert.Empty(validationResult.Errors);
        Assert.Equal(new ValidOrder("VIIA4", Side.Sell, 250, 35.10m), validationResult.Order);
    }

    [Fact]
    public void DeveListarUmErroPorCampoQuandoTudoEstaInvalidoNoFix()
    {
        var validationResult = OrderValidator.ValidateFromFix("XPTO", '9', 1.5m, 10.005m);

        Assert.Null(validationResult.Order);
        Assert.Equal(
            [
                new FieldError(OrderFields.Symbol, OrderMessages.SymbolInvalid),
                new FieldError(OrderFields.Side, OrderMessages.SideInvalid),
                new FieldError(OrderFields.Quantity, OrderMessages.QuantityNotInteger),
                new FieldError(OrderFields.Price, OrderMessages.PriceOffTick)
            ],
            validationResult.Errors);
    }

    [Fact]
    public void DeveTerSoOsTresSimbolosDoEnunciado()
    {
        Assert.Equal(["PETR4", "VALE3", "VIIA4"], OrderRules.Symbols);
    }
}
