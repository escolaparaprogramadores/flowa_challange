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
    private const NumberStyles JsonNumberStyle = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;

    // Entrada da tela: os campos chegam como texto cru do JSON.
    public static OrderValidationResult ValidateFromJson(string? symbol, string? side, string? quantity, string? price)
    {
        var errors = new List<FieldError>();

        var validSymbol = CheckSymbol(symbol, errors);
        var validSide = ParseJsonSide(side, errors);
        var validQuantity = ParseQuantity(quantity, errors);
        var validPrice = ParsePrice(price, errors);

        return BuildValidationResult(validSymbol, validSide, validQuantity, validPrice, errors);
    }

    // Entrada do FIX: o lado chega como o caractere da tag 54; quantidade e preço como decimal.
    public static OrderValidationResult ValidateFromFix(string? symbol, char side, decimal quantity, decimal price)
    {
        var errors = new List<FieldError>();

        var validSymbol = CheckSymbol(symbol, errors);
        var validSide = ParseFixSide(side, errors);
        var validQuantity = CheckQuantity(quantity, errors);
        var validPrice = CheckPrice(price, errors);

        return BuildValidationResult(validSymbol, validSide, validQuantity, validPrice, errors);
    }

    private static OrderValidationResult BuildValidationResult(
        string? symbol, Side? side, int? quantity, decimal? price, List<FieldError> errors)
    {
        if (errors.Count > 0)
            return new OrderValidationResult(null, errors);

        return new OrderValidationResult(new ValidOrder(symbol!, side!.Value, quantity!.Value, price!.Value), errors);
    }

    private static string? CheckSymbol(string? symbol, List<FieldError> errors)
    {
        if (string.IsNullOrEmpty(symbol))
        {
            errors.Add(new FieldError(OrderFields.Symbol, OrderMessages.SymbolRequired));
            return null;
        }

        if (!OrderRules.Symbols.Contains(symbol))
        {
            errors.Add(new FieldError(OrderFields.Symbol, OrderMessages.SymbolInvalid));
            return null;
        }

        return symbol;
    }

    private static Side? ParseJsonSide(string? side, List<FieldError> errors) => side switch
    {
        SideCodes.BuyJson => Side.Buy,
        SideCodes.SellJson => Side.Sell,
        null or "" => AddFieldError<Side>(errors, OrderFields.Side, OrderMessages.SideRequired),
        _ => AddFieldError<Side>(errors, OrderFields.Side, OrderMessages.SideInvalid)
    };

    private static Side? ParseFixSide(char side, List<FieldError> errors) => side switch
    {
        SideCodes.BuyFix => Side.Buy,
        SideCodes.SellFix => Side.Sell,
        _ => AddFieldError<Side>(errors, OrderFields.Side, OrderMessages.SideInvalid)
    };

    private static int? ParseQuantity(string? quantity, List<FieldError> errors)
    {
        if (string.IsNullOrEmpty(quantity))
            return AddFieldError<int>(errors, OrderFields.Quantity, OrderMessages.QuantityRequired);

        if (!decimal.TryParse(quantity, JsonNumberStyle, CultureInfo.InvariantCulture, out var parsedQuantity))
        {
            // Número que nem cabe no decimal é grande demais, não "texto".
            if (IsNumberBeyondDecimal(quantity, out var isNegative))
                return AddFieldError<int>(errors, OrderFields.Quantity,
                    isNegative ? OrderMessages.QuantityNotPositive : OrderMessages.QuantityTooLarge);

            return AddFieldError<int>(errors, OrderFields.Quantity, OrderMessages.QuantityNotInteger);
        }

        if (SignificantDecimalPlaces(quantity) > 0)
            return AddFieldError<int>(errors, OrderFields.Quantity, OrderMessages.QuantityNotInteger);

        return CheckQuantity(parsedQuantity, errors);
    }

    private static int? CheckQuantity(decimal quantity, List<FieldError> errors)
    {
        if (decimal.Truncate(quantity) != quantity)
            return AddFieldError<int>(errors, OrderFields.Quantity, OrderMessages.QuantityNotInteger);

        if (quantity <= 0)
            return AddFieldError<int>(errors, OrderFields.Quantity, OrderMessages.QuantityNotPositive);

        if (quantity >= OrderRules.MaxQuantityExclusive)
            return AddFieldError<int>(errors, OrderFields.Quantity, OrderMessages.QuantityTooLarge);

        return (int)quantity;
    }

    private static decimal? ParsePrice(string? price, List<FieldError> errors)
    {
        if (string.IsNullOrEmpty(price))
            return AddFieldError<decimal>(errors, OrderFields.Price, OrderMessages.PriceRequired);

        if (!decimal.TryParse(price, JsonNumberStyle, CultureInfo.InvariantCulture, out var parsedPrice))
        {
            if (IsNumberBeyondDecimal(price, out var isNegative))
                return AddFieldError<decimal>(errors, OrderFields.Price,
                    isNegative ? OrderMessages.PriceNotPositive : OrderMessages.PriceTooLarge);

            return AddFieldError<decimal>(errors, OrderFields.Price, OrderMessages.PriceNotNumber);
        }

        var checkedPrice = CheckPrice(parsedPrice, errors);

        if (checkedPrice is not null && SignificantDecimalPlaces(price) > OrderRules.PriceTick.Scale)
            return AddFieldError<decimal>(errors, OrderFields.Price, OrderMessages.PriceOffTick);

        return checkedPrice;
    }

    private static decimal? CheckPrice(decimal price, List<FieldError> errors)
    {
        if (price <= 0)
            return AddFieldError<decimal>(errors, OrderFields.Price, OrderMessages.PriceNotPositive);

        if (price >= OrderRules.MaxPriceExclusive)
            return AddFieldError<decimal>(errors, OrderFields.Price, OrderMessages.PriceTooLarge);

        // decimal é exato na base 10, então o resto da divisão diz se está no passo de 0,01.
        if (price % OrderRules.PriceTick != 0)
            return AddFieldError<decimal>(errors, OrderFields.Price, OrderMessages.PriceOffTick);

        return price;
    }

    // Casas decimais que contam (sem zeros à direita), lidas no texto: o decimal arredonda o que
    // passa de 28 dígitos e faria "10.0000000000000000000000000001" virar 10.
    private static int SignificantDecimalPlaces(string number)
    {
        var point = number.IndexOf('.');
        return point < 0 ? 0 : number[(point + 1)..].TrimEnd('0').Length;
    }

    private static bool IsNumberBeyondDecimal(string number, out bool isNegative)
    {
        var isNumber = double.TryParse(number, JsonNumberStyle, CultureInfo.InvariantCulture, out var approximateValue);
        isNegative = approximateValue < 0;
        return isNumber;
    }

    private static T? AddFieldError<T>(List<FieldError> errors, string field, string message) where T : struct
    {
        errors.Add(new FieldError(field, message));
        return null;
    }
}
