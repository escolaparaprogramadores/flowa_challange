namespace Flowa.Shared;

// Regras de campo do enunciado, usadas pelos dois apps.
// O limite de exposição não mora aqui: é constante do OrderAccumulator.
public static class OrderRules
{
    public static readonly IReadOnlyList<string> AllowedOrderSymbols = ["PETR4", "VALE3", "VIIA4"];

    public const int MaxOrderQuantityExclusive = 100_000;
    public const decimal MaxOrderPriceExclusive = 1_000m;
    public const decimal OrderPriceTick = 0.01m;
}

public enum OrderSide
{
    Buy,
    Sell
}

public static class OrderSideCodes
{
    public const string BuyOrderSideJsonCode = "buy";
    public const string SellOrderSideJsonCode = "sell";
    public const char BuyOrderSideFixCode = '1';
    public const char SellOrderSideFixCode = '2';

    public static char ToFixOrderSide(this OrderSide orderSide) => orderSide == OrderSide.Buy ? BuyOrderSideFixCode : SellOrderSideFixCode;

    public static string ToJsonOrderSide(this OrderSide orderSide) => orderSide == OrderSide.Buy ? BuyOrderSideJsonCode : SellOrderSideJsonCode;
}

public static class OrderFields
{
    public const string OrderSymbolFieldName = "symbol";
    public const string OrderSideFieldName = "side";
    public const string OrderQuantityFieldName = "quantity";
    public const string OrderPriceFieldName = "price";
}

public static class OrderMessages
{
    public const string OrderSymbolRequiredMessage = "Informe o símbolo.";
    public const string OrderSymbolInvalidMessage = "Símbolo inválido. Use PETR4, VALE3 ou VIIA4.";

    public const string OrderSideRequiredMessage = "Informe o lado da ordem.";
    public const string OrderSideInvalidMessage = "Lado inválido. Use compra ou venda.";

    public const string OrderQuantityRequiredMessage = "Informe a quantidade.";
    public const string OrderQuantityNotIntegerMessage = "A quantidade deve ser um número inteiro.";
    public const string OrderQuantityNotPositiveMessage = "A quantidade deve ser maior que zero.";
    public const string OrderQuantityTooLargeMessage = "A quantidade deve ser menor que 100.000.";

    public const string OrderPriceRequiredMessage = "Informe o preço.";
    public const string OrderPriceNotNumberMessage = "O preço deve ser um número.";
    public const string OrderPriceNotPositiveMessage = "O preço deve ser maior que zero.";
    public const string OrderPriceTooLargeMessage = "O preço deve ser menor que 1.000,00.";
    public const string OrderPriceOffTickMessage = "O preço deve ser múltiplo de 0,01.";
}
