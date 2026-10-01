using System.Globalization;

namespace Flowa.Shared;

public sealed record FieldError(string Field, string Message);

public sealed record ValidOrder(string Symbol, Side Side, int Quantity, decimal Price);

public sealed record OrderValidationResult(ValidOrder? Order, IReadOnlyList<FieldError> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public static class OrderValidator
{
    // Ponto como separador decimal e nada de milhar: é o formato do número no JSON.
    private const NumberStyles NumberFormat = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;

    // Entrada da tela: os campos chegam como texto cru do JSON.
    public static OrderValidationResult Validate(string? symbol, string? side, string? quantity, string? price)
    {
        var errors = new List<FieldError>();

        var validSymbol = CheckSymbol(symbol, errors);
        var validSide = ParseJsonSide(side, errors);
        var validQuantity = ParseQuantity(quantity, errors);
        var validPrice = ParsePrice(price, errors);

        return Result(validSymbol, validSide, validQuantity, validPrice, errors);
    }

    // Entrada do FIX: o lado chega como o caractere da tag 54; quantidade e preço como decimal.
    public static OrderValidationResult Validate(string? symbol, char side, decimal quantity, decimal price)
    {
        var errors = new List<FieldError>();

        var validSymbol = CheckSymbol(symbol, errors);
        var validSide = ParseFixSide(side, errors);
        var validQuantity = CheckQuantity(quantity, errors);
        var validPrice = CheckPrice(price, errors);

        return Result(validSymbol, validSide, validQuantity, validPrice, errors);
    }

    private static OrderValidationResult Result(
        string? symbol, Side? side, int? quantity, decimal? price, List<FieldError> errors)
    {
        if (errors.Count > 0)
            return new OrderValidationResult(null, errors);

        return new OrderValidationResult(new ValidOrder(symbol!, side!.Value, quantity!.Value, price!.Value), errors);
    }

    private static string? CheckSymbol(string? symbol, List<FieldError> errors)
    {
        if (string.IsNullOrEmpty(symbol))
            return Fail<string>(errors, OrderFields.Symbol, OrderMessages.SymbolRequired);

        if (!OrderRules.Symbols.Contains(symbol))
            return Fail<string>(errors, OrderFields.Symbol, OrderMessages.SymbolInvalid);

        return symbol;
    }

    private static Side? ParseJsonSide(string? side, List<FieldError> errors) => side switch
    {
        SideCodes.BuyJson => Side.Buy,
        SideCodes.SellJson => Side.Sell,
        null or "" => FailValue<Side>(errors, OrderFields.Side, OrderMessages.SideRequired),
        _ => FailValue<Side>(errors, OrderFields.Side, OrderMessages.SideInvalid)
    };

    private static Side? ParseFixSide(char side, List<FieldError> errors) => side switch
    {
        SideCodes.BuyFix => Side.Buy,
        SideCodes.SellFix => Side.Sell,
        _ => FailValue<Side>(errors, OrderFields.Side, OrderMessages.SideInvalid)
    };

    private static int? ParseQuantity(string? quantity, List<FieldError> errors)
    {
        if (string.IsNullOrEmpty(quantity))
            return FailValue<int>(errors, OrderFields.Quantity, OrderMessages.QuantityRequired);

        if (!decimal.TryParse(quantity, NumberFormat, CultureInfo.InvariantCulture, out var value))
            return FailValue<int>(errors, OrderFields.Quantity, OrderMessages.QuantityNotInteger);

        return CheckQuantity(value, errors);
    }

    private static int? CheckQuantity(decimal quantity, List<FieldError> errors)
    {
        if (decimal.Truncate(quantity) != quantity)
            return FailValue<int>(errors, OrderFields.Quantity, OrderMessages.QuantityNotInteger);

        if (quantity <= 0)
            return FailValue<int>(errors, OrderFields.Quantity, OrderMessages.QuantityNotPositive);

        if (quantity >= OrderRules.MaxQuantityExclusive)
            return FailValue<int>(errors, OrderFields.Quantity, OrderMessages.QuantityTooLarge);

        return (int)quantity;
    }

    private static decimal? ParsePrice(string? price, List<FieldError> errors)
    {
        if (string.IsNullOrEmpty(price))
            return FailValue<decimal>(errors, OrderFields.Price, OrderMessages.PriceRequired);

        if (!decimal.TryParse(price, NumberFormat, CultureInfo.InvariantCulture, out var value))
            return FailValue<decimal>(errors, OrderFields.Price, OrderMessages.PriceNotNumber);

        return CheckPrice(value, errors);
    }

    private static decimal? CheckPrice(decimal price, List<FieldError> errors)
    {
        if (price <= 0)
            return FailValue<decimal>(errors, OrderFields.Price, OrderMessages.PriceNotPositive);

        if (price >= OrderRules.MaxPriceExclusive)
            return FailValue<decimal>(errors, OrderFields.Price, OrderMessages.PriceTooLarge);

        // decimal é exato na base 10, então o resto da divisão diz se está no passo de 0,01.
        if (price % OrderRules.PriceTick != 0)
            return FailValue<decimal>(errors, OrderFields.Price, OrderMessages.PriceOffTick);

        return price;
    }

    private static T? Fail<T>(List<FieldError> errors, string field, string message) where T : class
    {
        errors.Add(new FieldError(field, message));
        return null;
    }

    private static T? FailValue<T>(List<FieldError> errors, string field, string message) where T : struct
    {
        errors.Add(new FieldError(field, message));
        return null;
    }
}
