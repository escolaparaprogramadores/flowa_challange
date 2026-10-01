namespace Flowa.Shared;

// Regras de campo do enunciado, usadas pelos dois apps.
// O limite de exposição não mora aqui: é constante do OrderAccumulator.
public static class OrderRules
{
    public static readonly IReadOnlyList<string> Symbols = ["PETR4", "VALE3", "VIIA4"];

    public const int MaxQuantityExclusive = 100_000;
    public const decimal MaxPriceExclusive = 1_000m;
    public const decimal PriceTick = 0.01m;
}

public enum Side
{
    Buy,
    Sell
}

public static class SideCodes
{
    public const string BuyJson = "buy";
    public const string SellJson = "sell";
    public const char BuyFix = '1';
    public const char SellFix = '2';

    public static char ToFix(this Side side) => side == Side.Buy ? BuyFix : SellFix;

    public static string ToJson(this Side side) => side == Side.Buy ? BuyJson : SellJson;
}

public static class OrderFields
{
    public const string Symbol = "symbol";
    public const string Side = "side";
    public const string Quantity = "quantity";
    public const string Price = "price";
}

public static class OrderMessages
{
    public const string SymbolRequired = "Informe o símbolo.";
    public const string SymbolInvalid = "Símbolo inválido. Use PETR4, VALE3 ou VIIA4.";

    public const string SideRequired = "Informe o lado da ordem.";
    public const string SideInvalid = "Lado inválido. Use compra ou venda.";

    public const string QuantityRequired = "Informe a quantidade.";
    public const string QuantityNotInteger = "A quantidade deve ser um número inteiro.";
    public const string QuantityNotPositive = "A quantidade deve ser maior que zero.";
    public const string QuantityTooLarge = "A quantidade deve ser menor que 100.000.";

    public const string PriceRequired = "Informe o preço.";
    public const string PriceNotNumber = "O preço deve ser um número.";
    public const string PriceNotPositive = "O preço deve ser maior que zero.";
    public const string PriceTooLarge = "O preço deve ser menor que 1.000,00.";
    public const string PriceOffTick = "O preço deve ser múltiplo de 0,01.";
}
