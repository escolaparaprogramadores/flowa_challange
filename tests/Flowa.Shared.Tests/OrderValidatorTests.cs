using System.Text.Json;
using Flowa.Shared;

namespace Flowa.Shared.Tests;

public class OrderValidatorTests
{
    // Ordem válida de base; cada teste troca só o campo que está provando.
    private const string ValidOrderSymbol = "PETR4";
    private const string ValidOrderSideText = "buy";
    private const string ValidOrderQuantityText = "100";
    private const string ValidOrderPriceText = "10.50";

    private static void AssertOrderRejectedWithSingleOrderFieldError(OrderValidationResult orderValidationResult, string orderField, string orderFieldErrorMessage)
    {
        Assert.False(orderValidationResult.IsOrderValid);
        Assert.Null(orderValidationResult.ValidatedOrder);
        Assert.Equal(new OrderFieldError(orderField, orderFieldErrorMessage), Assert.Single(orderValidationResult.OrderFieldErrors));
    }

    // Regra: símbolo

    [Theory]
    [InlineData("PETR4")]
    [InlineData("VALE3")]
    [InlineData("VIIA4")]
    public void DeveAceitarSimboloDeOrdemPermitido(string orderSymbol)
    {
        var orderValidationResult = OrderValidator.ValidateOrderFromJson(orderSymbol, ValidOrderSideText, ValidOrderQuantityText, ValidOrderPriceText);

        Assert.True(orderValidationResult.IsOrderValid);
        Assert.Equal(orderSymbol, orderValidationResult.ValidatedOrder!.OrderSymbol);
    }

    [Theory]
    [InlineData("petr4", OrderMessages.OrderSymbolInvalidMessage)]
    [InlineData("ABCD3", OrderMessages.OrderSymbolInvalidMessage)]
    [InlineData("PETR4 ", OrderMessages.OrderSymbolInvalidMessage)]
    [InlineData("", OrderMessages.OrderSymbolRequiredMessage)]
    [InlineData(null, OrderMessages.OrderSymbolRequiredMessage)]
    public void DeveRecusarSimboloDeOrdemForaDaListaOuVazio(string? orderSymbol, string expectedOrderFieldErrorMessage)
    {
        var orderValidationResult = OrderValidator.ValidateOrderFromJson(orderSymbol, ValidOrderSideText, ValidOrderQuantityText, ValidOrderPriceText);

        AssertOrderRejectedWithSingleOrderFieldError(orderValidationResult, OrderFields.OrderSymbolFieldName, expectedOrderFieldErrorMessage);
    }

    // Regra: lado

    [Theory]
    [InlineData("buy", OrderSide.Buy, '1')]
    [InlineData("sell", OrderSide.Sell, '2')]
    public void DeveConverterLadoDaOrdemDaTelaParaFix(string orderSide, OrderSide expectedOrderSide, char expectedOrderSideFixCode)
    {
        var orderValidationResult = OrderValidator.ValidateOrderFromJson(ValidOrderSymbol, orderSide, ValidOrderQuantityText, ValidOrderPriceText);

        Assert.True(orderValidationResult.IsOrderValid);
        Assert.Equal(expectedOrderSide, orderValidationResult.ValidatedOrder!.OrderSide);
        Assert.Equal(expectedOrderSideFixCode, orderValidationResult.ValidatedOrder.OrderSide.ToFixOrderSide());
        Assert.Equal(orderSide, orderValidationResult.ValidatedOrder.OrderSide.ToJsonOrderSide());
    }

    [Theory]
    [InlineData('1', OrderSide.Buy)]
    [InlineData('2', OrderSide.Sell)]
    public void DeveAceitarLadoDaOrdemFix(char orderSide, OrderSide expectedOrderSide)
    {
        var orderValidationResult = OrderValidator.ValidateOrderFromFix(ValidOrderSymbol, orderSide, 100m, 10.50m);

        Assert.True(orderValidationResult.IsOrderValid);
        Assert.Equal(expectedOrderSide, orderValidationResult.ValidatedOrder!.OrderSide);
    }

    [Theory]
    [InlineData("BUY", OrderMessages.OrderSideInvalidMessage)]
    [InlineData("compra", OrderMessages.OrderSideInvalidMessage)]
    [InlineData("1", OrderMessages.OrderSideInvalidMessage)]
    [InlineData("short", OrderMessages.OrderSideInvalidMessage)]
    [InlineData("", OrderMessages.OrderSideRequiredMessage)]
    [InlineData(null, OrderMessages.OrderSideRequiredMessage)]
    public void DeveRecusarLadoDaOrdemDesconhecidoOuVazio(string? orderSide, string expectedOrderFieldErrorMessage)
    {
        var orderValidationResult = OrderValidator.ValidateOrderFromJson(ValidOrderSymbol, orderSide, ValidOrderQuantityText, ValidOrderPriceText);

        AssertOrderRejectedWithSingleOrderFieldError(orderValidationResult, OrderFields.OrderSideFieldName, expectedOrderFieldErrorMessage);
    }

    [Theory]
    [InlineData('0')]
    [InlineData('3')]
    [InlineData('5')]
    public void DeveRecusarLadoDaOrdemFixDiferenteDeCompraEVenda(char orderSide)
    {
        var orderValidationResult = OrderValidator.ValidateOrderFromFix(ValidOrderSymbol, orderSide, 100m, 10.50m);

        AssertOrderRejectedWithSingleOrderFieldError(orderValidationResult, OrderFields.OrderSideFieldName, OrderMessages.OrderSideInvalidMessage);
    }

    // Regra: quantidade

    // "100.0" e "+5" têm valor inteiro: o número JSON 100.0 é o mesmo que 100.
    [Theory]
    [InlineData("1", 1)]
    [InlineData("99999", 99999)]
    [InlineData("100.0", 100)]
    [InlineData("+5", 5)]
    public void DeveAceitarQuantidadeDaOrdemNasBordas(string orderQuantity, int expectedOrderQuantity)
    {
        var orderValidationResult = OrderValidator.ValidateOrderFromJson(ValidOrderSymbol, ValidOrderSideText, orderQuantity, ValidOrderPriceText);

        Assert.True(orderValidationResult.IsOrderValid);
        Assert.Equal(expectedOrderQuantity, orderValidationResult.ValidatedOrder!.OrderQuantity);
    }

    [Theory]
    [InlineData("0", OrderMessages.OrderQuantityNotPositiveMessage)]
    [InlineData("-1", OrderMessages.OrderQuantityNotPositiveMessage)]
    [InlineData("1.5", OrderMessages.OrderQuantityNotIntegerMessage)]
    [InlineData("abc", OrderMessages.OrderQuantityNotIntegerMessage)]
    [InlineData("100000", OrderMessages.OrderQuantityTooLargeMessage)]
    [InlineData("99999999999999999999999999999999", OrderMessages.OrderQuantityTooLargeMessage)]
    [InlineData("-99999999999999999999999999999999", OrderMessages.OrderQuantityNotPositiveMessage)]
    [InlineData(" 100", OrderMessages.OrderQuantityNotIntegerMessage)]
    [InlineData("100 ", OrderMessages.OrderQuantityNotIntegerMessage)]
    [InlineData("1e3", OrderMessages.OrderQuantityNotIntegerMessage)]
    [InlineData("99999.00000000000000000000000001", OrderMessages.OrderQuantityNotIntegerMessage)]
    [InlineData("", OrderMessages.OrderQuantityRequiredMessage)]
    [InlineData(null, OrderMessages.OrderQuantityRequiredMessage)]
    public void DeveRecusarQuantidadeDaOrdemInvalida(string? orderQuantity, string expectedOrderFieldErrorMessage)
    {
        var orderValidationResult = OrderValidator.ValidateOrderFromJson(ValidOrderSymbol, ValidOrderSideText, orderQuantity, ValidOrderPriceText);

        AssertOrderRejectedWithSingleOrderFieldError(orderValidationResult, OrderFields.OrderQuantityFieldName, expectedOrderFieldErrorMessage);
    }

    public static TheoryData<decimal, int> QuantidadesDeOrdemFixAceitas => new() { { 1m, 1 }, { 99999m, 99999 } };

    [Theory]
    [MemberData(nameof(QuantidadesDeOrdemFixAceitas))]
    public void DeveAceitarQuantidadeDaOrdemFixNasBordas(decimal orderQuantity, int expectedOrderQuantity)
    {
        var orderValidationResult = OrderValidator.ValidateOrderFromFix(ValidOrderSymbol, OrderSideCodes.BuyOrderSideFixCode, orderQuantity, 10.50m);

        Assert.True(orderValidationResult.IsOrderValid);
        Assert.Equal(expectedOrderQuantity, orderValidationResult.ValidatedOrder!.OrderQuantity);
    }

    public static TheoryData<decimal, string> QuantidadesDeOrdemFixRecusadas => new()
    {
        { 0m, OrderMessages.OrderQuantityNotPositiveMessage },
        { -1m, OrderMessages.OrderQuantityNotPositiveMessage },
        { 1.5m, OrderMessages.OrderQuantityNotIntegerMessage },
        { 100000m, OrderMessages.OrderQuantityTooLargeMessage }
    };

    [Theory]
    [MemberData(nameof(QuantidadesDeOrdemFixRecusadas))]
    public void DeveRecusarQuantidadeDaOrdemFixInvalida(decimal orderQuantity, string expectedOrderFieldErrorMessage)
    {
        var orderValidationResult = OrderValidator.ValidateOrderFromFix(ValidOrderSymbol, OrderSideCodes.BuyOrderSideFixCode, orderQuantity, 10.50m);

        AssertOrderRejectedWithSingleOrderFieldError(orderValidationResult, OrderFields.OrderQuantityFieldName, expectedOrderFieldErrorMessage);
    }

    // Regra: preço

    // "10.500" prova que zero à direita do centavo não conta como casa a mais.
    public static TheoryData<string, decimal> PrecosDeOrdemAceitos => new() { { "0.01", 0.01m }, { "999.99", 999.99m }, { "10.500", 10.50m } };

    [Theory]
    [MemberData(nameof(PrecosDeOrdemAceitos))]
    public void DeveAceitarPrecoDaOrdemNasBordasNasDuasEntradas(string orderPriceText, decimal orderPrice)
    {
        var jsonOrderValidationResult = OrderValidator.ValidateOrderFromJson(ValidOrderSymbol, ValidOrderSideText, ValidOrderQuantityText, orderPriceText);
        var fixOrderValidationResult = OrderValidator.ValidateOrderFromFix(ValidOrderSymbol, OrderSideCodes.BuyOrderSideFixCode, 100m, orderPrice);

        Assert.True(jsonOrderValidationResult.IsOrderValid);
        Assert.Equal(orderPrice, jsonOrderValidationResult.ValidatedOrder!.OrderPrice);
        Assert.True(fixOrderValidationResult.IsOrderValid);
        Assert.Equal(orderPrice, fixOrderValidationResult.ValidatedOrder!.OrderPrice);
    }

    [Theory]
    [InlineData("0", OrderMessages.OrderPriceNotPositiveMessage)]
    [InlineData("-0.01", OrderMessages.OrderPriceNotPositiveMessage)]
    [InlineData("1000", OrderMessages.OrderPriceTooLargeMessage)]
    [InlineData("10.005", OrderMessages.OrderPriceOffTickMessage)]
    [InlineData("10.0000000000000000000000000001", OrderMessages.OrderPriceOffTickMessage)]
    [InlineData("abc", OrderMessages.OrderPriceNotNumberMessage)]
    [InlineData("10,005", OrderMessages.OrderPriceNotNumberMessage)]
    [InlineData(" 10.50", OrderMessages.OrderPriceNotNumberMessage)]
    [InlineData("99999999999999999999999999999999", OrderMessages.OrderPriceTooLargeMessage)]
    [InlineData("-99999999999999999999999999999999", OrderMessages.OrderPriceNotPositiveMessage)]
    [InlineData("", OrderMessages.OrderPriceRequiredMessage)]
    [InlineData(null, OrderMessages.OrderPriceRequiredMessage)]
    public void DeveRecusarPrecoDaOrdemInvalido(string? orderPrice, string expectedOrderFieldErrorMessage)
    {
        var orderValidationResult = OrderValidator.ValidateOrderFromJson(ValidOrderSymbol, ValidOrderSideText, ValidOrderQuantityText, orderPrice);

        AssertOrderRejectedWithSingleOrderFieldError(orderValidationResult, OrderFields.OrderPriceFieldName, expectedOrderFieldErrorMessage);
    }

    public static TheoryData<decimal, string> PrecosDeOrdemFixRecusados => new()
    {
        { 0m, OrderMessages.OrderPriceNotPositiveMessage },
        { -0.01m, OrderMessages.OrderPriceNotPositiveMessage },
        { 1000m, OrderMessages.OrderPriceTooLargeMessage },
        { 10.005m, OrderMessages.OrderPriceOffTickMessage }
    };

    [Theory]
    [MemberData(nameof(PrecosDeOrdemFixRecusados))]
    public void DeveRecusarPrecoDaOrdemFixInvalido(decimal orderPrice, string expectedOrderFieldErrorMessage)
    {
        var orderValidationResult = OrderValidator.ValidateOrderFromFix(ValidOrderSymbol, OrderSideCodes.BuyOrderSideFixCode, 100m, orderPrice);

        AssertOrderRejectedWithSingleOrderFieldError(orderValidationResult, OrderFields.OrderPriceFieldName, expectedOrderFieldErrorMessage);
    }

    // Ordem inteira

    [Fact]
    public void DeveDevolverOrdemTipadaQuandoTodosOsCamposDaOrdemSaoValidos()
    {
        var orderValidationResult = OrderValidator.ValidateOrderFromJson("VALE3", "sell", "250", "35.10");

        Assert.True(orderValidationResult.IsOrderValid);
        Assert.Empty(orderValidationResult.OrderFieldErrors);
        Assert.Equal(new ValidOrder("VALE3", OrderSide.Sell, 250, 35.10m), orderValidationResult.ValidatedOrder);
    }

    [Fact]
    public void DeveListarUmErroPorCampoDaOrdemQuandoTodosSaoInvalidos()
    {
        var orderValidationResult = OrderValidator.ValidateOrderFromJson("XPTO", "hold", "0", "1000");

        Assert.Null(orderValidationResult.ValidatedOrder);
        Assert.Equal(
            [
                new OrderFieldError(OrderFields.OrderSymbolFieldName, OrderMessages.OrderSymbolInvalidMessage),
                new OrderFieldError(OrderFields.OrderSideFieldName, OrderMessages.OrderSideInvalidMessage),
                new OrderFieldError(OrderFields.OrderQuantityFieldName, OrderMessages.OrderQuantityNotPositiveMessage),
                new OrderFieldError(OrderFields.OrderPriceFieldName, OrderMessages.OrderPriceTooLargeMessage)
            ],
            orderValidationResult.OrderFieldErrors);
    }

    [Fact]
    public void DeveDevolverOrdemTipadaQuandoTodosOsCamposDaOrdemSaoValidosNoFix()
    {
        var orderValidationResult = OrderValidator.ValidateOrderFromFix("VIIA4", OrderSideCodes.SellOrderSideFixCode, 250m, 35.10m);

        Assert.True(orderValidationResult.IsOrderValid);
        Assert.Empty(orderValidationResult.OrderFieldErrors);
        Assert.Equal(new ValidOrder("VIIA4", OrderSide.Sell, 250, 35.10m), orderValidationResult.ValidatedOrder);
    }

    [Fact]
    public void DeveListarUmErroPorCampoDaOrdemQuandoTodosSaoInvalidosNoFix()
    {
        var orderValidationResult = OrderValidator.ValidateOrderFromFix("XPTO", '9', 1.5m, 10.005m);

        Assert.Null(orderValidationResult.ValidatedOrder);
        Assert.Equal(
            [
                new OrderFieldError(OrderFields.OrderSymbolFieldName, OrderMessages.OrderSymbolInvalidMessage),
                new OrderFieldError(OrderFields.OrderSideFieldName, OrderMessages.OrderSideInvalidMessage),
                new OrderFieldError(OrderFields.OrderQuantityFieldName, OrderMessages.OrderQuantityNotIntegerMessage),
                new OrderFieldError(OrderFields.OrderPriceFieldName, OrderMessages.OrderPriceOffTickMessage)
            ],
            orderValidationResult.OrderFieldErrors);
    }

    // A resposta de erro da API serializa este registro direto: os nomes do contrato não podem mudar.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeveSerializarErroDeCampoDaOrdemComOsNomesDoContrato(bool useWebJsonOptions)
    {
        var orderFieldError = new OrderFieldError(OrderFields.OrderPriceFieldName, OrderMessages.OrderPriceOffTickMessage);
        var orderJsonOptions = useWebJsonOptions ? JsonSerializerOptions.Web : JsonSerializerOptions.Default;

        using var orderFieldErrorJson = JsonDocument.Parse(JsonSerializer.Serialize(orderFieldError, orderJsonOptions));

        Assert.Equal(["field", "message"], orderFieldErrorJson.RootElement.EnumerateObject().Select(jsonProperty => jsonProperty.Name));
        Assert.Equal("price", orderFieldErrorJson.RootElement.GetProperty("field").GetString());
        Assert.Equal("O preço deve ser múltiplo de 0,01.", orderFieldErrorJson.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public void DeveTerSoOsTresSimbolosDeOrdemDoEnunciado()
    {
        Assert.Equal(["PETR4", "VALE3", "VIIA4"], OrderRules.AllowedOrderSymbols);
    }
}
