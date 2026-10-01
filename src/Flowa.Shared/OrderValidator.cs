using System.Globalization;

namespace Flowa.Shared;

public sealed record OrderFieldError(string Field, string Message);

public sealed record ValidOrder(string Symbol, OrderSide Side, int Quantity, decimal Price);

public sealed record OrderValidationResult(ValidOrder? Order, IReadOnlyList<OrderFieldError> Errors)
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
        var orderFieldErrors = new List<OrderFieldError>();

        var validSymbol = CheckSymbol(symbol, orderFieldErrors);
        var validSide = ParseJsonSide(side, orderFieldErrors);
        var validQuantity = ParseQuantity(quantity, orderFieldErrors);
        var validPrice = ParsePrice(price, orderFieldErrors);

        return BuildValidationResult(validSymbol, validSide, validQuantity, validPrice, orderFieldErrors);
    }

    // Entrada do FIX: o lado chega como o caractere da tag 54; quantidade e preço como decimal.
    public static OrderValidationResult ValidateFromFix(string? symbol, char side, decimal quantity, decimal price)
    {
        var orderFieldErrors = new List<OrderFieldError>();

        var validSymbol = CheckSymbol(symbol, orderFieldErrors);
        var validSide = ParseFixSide(side, orderFieldErrors);
        var validQuantity = CheckQuantity(quantity, orderFieldErrors);
        var validPrice = CheckPrice(price, orderFieldErrors);

        return BuildValidationResult(validSymbol, validSide, validQuantity, validPrice, orderFieldErrors);
    }

    private static OrderValidationResult BuildValidationResult(
        string? symbol, OrderSide? side, int? quantity, decimal? price, List<OrderFieldError> orderFieldErrors)
    {
        if (orderFieldErrors.Count > 0)
            return new OrderValidationResult(null, orderFieldErrors);

        return new OrderValidationResult(new ValidOrder(symbol!, side!.Value, quantity!.Value, price!.Value), orderFieldErrors);
    }

    private static string? CheckSymbol(string? symbol, List<OrderFieldError> orderFieldErrors)
    {
        if (string.IsNullOrEmpty(symbol))
        {
            orderFieldErrors.Add(new OrderFieldError(OrderFields.Symbol, OrderMessages.SymbolRequired));
            return null;
        }

        if (!OrderRules.Symbols.Contains(symbol))
        {
            orderFieldErrors.Add(new OrderFieldError(OrderFields.Symbol, OrderMessages.SymbolInvalid));
            return null;
        }

        return symbol;
    }

    private static OrderSide? ParseJsonSide(string? side, List<OrderFieldError> orderFieldErrors) => side switch
    {
        OrderSideCodes.BuyJson => OrderSide.Buy,
        OrderSideCodes.SellJson => OrderSide.Sell,
        null or "" => AddOrderFieldError<OrderSide>(orderFieldErrors, OrderFields.Side, OrderMessages.SideRequired),
        _ => AddOrderFieldError<OrderSide>(orderFieldErrors, OrderFields.Side, OrderMessages.SideInvalid)
    };

    private static OrderSide? ParseFixSide(char side, List<OrderFieldError> orderFieldErrors) => side switch
    {
        OrderSideCodes.BuyFix => OrderSide.Buy,
        OrderSideCodes.SellFix => OrderSide.Sell,
        _ => AddOrderFieldError<OrderSide>(orderFieldErrors, OrderFields.Side, OrderMessages.SideInvalid)
    };

    private static int? ParseQuantity(string? quantity, List<OrderFieldError> orderFieldErrors)
    {
        if (string.IsNullOrEmpty(quantity))
            return AddOrderFieldError<int>(orderFieldErrors, OrderFields.Quantity, OrderMessages.QuantityRequired);

        if (!decimal.TryParse(quantity, JsonNumberStyle, CultureInfo.InvariantCulture, out var parsedQuantity))
        {
            // Número que nem cabe no decimal é grande demais, não "texto".
            if (IsNumberBeyondDecimal(quantity, out var isNegative))
                return AddOrderFieldError<int>(orderFieldErrors, OrderFields.Quantity,
                    isNegative ? OrderMessages.QuantityNotPositive : OrderMessages.QuantityTooLarge);

            return AddOrderFieldError<int>(orderFieldErrors, OrderFields.Quantity, OrderMessages.QuantityNotInteger);
        }

        if (SignificantDecimalPlaces(quantity) > 0)
            return AddOrderFieldError<int>(orderFieldErrors, OrderFields.Quantity, OrderMessages.QuantityNotInteger);

        return CheckQuantity(parsedQuantity, orderFieldErrors);
    }

    private static int? CheckQuantity(decimal quantity, List<OrderFieldError> orderFieldErrors)
    {
        if (decimal.Truncate(quantity) != quantity)
            return AddOrderFieldError<int>(orderFieldErrors, OrderFields.Quantity, OrderMessages.QuantityNotInteger);

        if (quantity <= 0)
            return AddOrderFieldError<int>(orderFieldErrors, OrderFields.Quantity, OrderMessages.QuantityNotPositive);

        if (quantity >= OrderRules.MaxQuantityExclusive)
            return AddOrderFieldError<int>(orderFieldErrors, OrderFields.Quantity, OrderMessages.QuantityTooLarge);

        return (int)quantity;
    }

    private static decimal? ParsePrice(string? price, List<OrderFieldError> orderFieldErrors)
    {
        if (string.IsNullOrEmpty(price))
            return AddOrderFieldError<decimal>(orderFieldErrors, OrderFields.Price, OrderMessages.PriceRequired);

        if (!decimal.TryParse(price, JsonNumberStyle, CultureInfo.InvariantCulture, out var parsedPrice))
        {
            if (IsNumberBeyondDecimal(price, out var isNegative))
                return AddOrderFieldError<decimal>(orderFieldErrors, OrderFields.Price,
                    isNegative ? OrderMessages.PriceNotPositive : OrderMessages.PriceTooLarge);

            return AddOrderFieldError<decimal>(orderFieldErrors, OrderFields.Price, OrderMessages.PriceNotNumber);
        }

        var checkedPrice = CheckPrice(parsedPrice, orderFieldErrors);

        if (checkedPrice is not null && SignificantDecimalPlaces(price) > OrderRules.PriceTick.Scale)
            return AddOrderFieldError<decimal>(orderFieldErrors, OrderFields.Price, OrderMessages.PriceOffTick);

        return checkedPrice;
    }

    private static decimal? CheckPrice(decimal price, List<OrderFieldError> orderFieldErrors)
    {
        if (price <= 0)
            return AddOrderFieldError<decimal>(orderFieldErrors, OrderFields.Price, OrderMessages.PriceNotPositive);

        if (price >= OrderRules.MaxPriceExclusive)
            return AddOrderFieldError<decimal>(orderFieldErrors, OrderFields.Price, OrderMessages.PriceTooLarge);

        // decimal é exato na base 10, então o resto da divisão diz se está no passo de 0,01.
        if (price % OrderRules.PriceTick != 0)
            return AddOrderFieldError<decimal>(orderFieldErrors, OrderFields.Price, OrderMessages.PriceOffTick);

        return price;
    }

    // Casas decimais que contam (sem zeros à direita), lidas no texto: o decimal arredonda o que
    // passa de 28 dígitos e faria "10.0000000000000000000000000001" virar 10.
    private static int SignificantDecimalPlaces(string fieldText)
    {
        var decimalSeparatorPosition = fieldText.IndexOf('.');
        return decimalSeparatorPosition < 0 ? 0 : fieldText[(decimalSeparatorPosition + 1)..].TrimEnd('0').Length;
    }

    private static bool IsNumberBeyondDecimal(string fieldText, out bool isNegative)
    {
        var isNumber = double.TryParse(fieldText, JsonNumberStyle, CultureInfo.InvariantCulture, out var approximateValue);
        isNegative = approximateValue < 0;
        return isNumber;
    }

    private static T? AddOrderFieldError<T>(List<OrderFieldError> orderFieldErrors, string field, string message) where T : struct
    {
        orderFieldErrors.Add(new OrderFieldError(field, message));
        return null;
    }
}
