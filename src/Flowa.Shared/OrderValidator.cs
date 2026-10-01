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
    private const NumberStyles OrderFieldJsonNumberStyle = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;

    // Entrada da tela: os campos chegam como texto cru do JSON.
    public static OrderValidationResult ValidateOrderFromJson(string? orderSymbol, string? orderSide, string? orderQuantity, string? orderPrice)
    {
        var orderFieldErrors = new List<OrderFieldError>();

        var validOrderSymbol = CheckOrderSymbol(orderSymbol, orderFieldErrors);
        var validOrderSide = ParseJsonOrderSide(orderSide, orderFieldErrors);
        var validOrderQuantity = ParseOrderQuantity(orderQuantity, orderFieldErrors);
        var validOrderPrice = ParseOrderPrice(orderPrice, orderFieldErrors);

        return BuildOrderValidationResult(validOrderSymbol, validOrderSide, validOrderQuantity, validOrderPrice, orderFieldErrors);
    }

    // Entrada do FIX: o lado chega como o caractere da tag 54; quantidade e preço como decimal.
    public static OrderValidationResult ValidateOrderFromFix(string? orderSymbol, char orderSide, decimal orderQuantity, decimal orderPrice)
    {
        var orderFieldErrors = new List<OrderFieldError>();

        var validOrderSymbol = CheckOrderSymbol(orderSymbol, orderFieldErrors);
        var validOrderSide = ParseFixOrderSide(orderSide, orderFieldErrors);
        var validOrderQuantity = CheckOrderQuantity(orderQuantity, orderFieldErrors);
        var validOrderPrice = CheckOrderPrice(orderPrice, orderFieldErrors);

        return BuildOrderValidationResult(validOrderSymbol, validOrderSide, validOrderQuantity, validOrderPrice, orderFieldErrors);
    }

    private static OrderValidationResult BuildOrderValidationResult(
        string? orderSymbol, OrderSide? orderSide, int? orderQuantity, decimal? orderPrice, List<OrderFieldError> orderFieldErrors)
    {
        if (orderFieldErrors.Count > 0)
            return new OrderValidationResult(null, orderFieldErrors);

        return new OrderValidationResult(new ValidOrder(orderSymbol!, orderSide!.Value, orderQuantity!.Value, orderPrice!.Value), orderFieldErrors);
    }

    private static string? CheckOrderSymbol(string? orderSymbol, List<OrderFieldError> orderFieldErrors)
    {
        if (string.IsNullOrEmpty(orderSymbol))
        {
            orderFieldErrors.Add(new OrderFieldError(OrderFields.Symbol, OrderMessages.SymbolRequired));
            return null;
        }

        if (!OrderRules.Symbols.Contains(orderSymbol))
        {
            orderFieldErrors.Add(new OrderFieldError(OrderFields.Symbol, OrderMessages.SymbolInvalid));
            return null;
        }

        return orderSymbol;
    }

    private static OrderSide? ParseJsonOrderSide(string? orderSide, List<OrderFieldError> orderFieldErrors) => orderSide switch
    {
        OrderSideCodes.BuyJson => OrderSide.Buy,
        OrderSideCodes.SellJson => OrderSide.Sell,
        null or "" => AddOrderFieldError<OrderSide>(orderFieldErrors, OrderFields.Side, OrderMessages.SideRequired),
        _ => AddOrderFieldError<OrderSide>(orderFieldErrors, OrderFields.Side, OrderMessages.SideInvalid)
    };

    private static OrderSide? ParseFixOrderSide(char orderSide, List<OrderFieldError> orderFieldErrors) => orderSide switch
    {
        OrderSideCodes.BuyFix => OrderSide.Buy,
        OrderSideCodes.SellFix => OrderSide.Sell,
        _ => AddOrderFieldError<OrderSide>(orderFieldErrors, OrderFields.Side, OrderMessages.SideInvalid)
    };

    private static int? ParseOrderQuantity(string? orderQuantity, List<OrderFieldError> orderFieldErrors)
    {
        if (string.IsNullOrEmpty(orderQuantity))
            return AddOrderFieldError<int>(orderFieldErrors, OrderFields.Quantity, OrderMessages.QuantityRequired);

        if (!decimal.TryParse(orderQuantity, OrderFieldJsonNumberStyle, CultureInfo.InvariantCulture, out var parsedOrderQuantity))
        {
            // Número que nem cabe no decimal é grande demais, não "texto".
            if (IsOrderFieldNumberBeyondDecimal(orderQuantity, out var isNegativeOrderFieldNumber))
                return AddOrderFieldError<int>(orderFieldErrors, OrderFields.Quantity,
                    isNegativeOrderFieldNumber ? OrderMessages.QuantityNotPositive : OrderMessages.QuantityTooLarge);

            return AddOrderFieldError<int>(orderFieldErrors, OrderFields.Quantity, OrderMessages.QuantityNotInteger);
        }

        if (SignificantDecimalPlacesInOrderFieldText(orderQuantity) > 0)
            return AddOrderFieldError<int>(orderFieldErrors, OrderFields.Quantity, OrderMessages.QuantityNotInteger);

        return CheckOrderQuantity(parsedOrderQuantity, orderFieldErrors);
    }

    private static int? CheckOrderQuantity(decimal orderQuantity, List<OrderFieldError> orderFieldErrors)
    {
        if (decimal.Truncate(orderQuantity) != orderQuantity)
            return AddOrderFieldError<int>(orderFieldErrors, OrderFields.Quantity, OrderMessages.QuantityNotInteger);

        if (orderQuantity <= 0)
            return AddOrderFieldError<int>(orderFieldErrors, OrderFields.Quantity, OrderMessages.QuantityNotPositive);

        if (orderQuantity >= OrderRules.MaxQuantityExclusive)
            return AddOrderFieldError<int>(orderFieldErrors, OrderFields.Quantity, OrderMessages.QuantityTooLarge);

        return (int)orderQuantity;
    }

    private static decimal? ParseOrderPrice(string? orderPrice, List<OrderFieldError> orderFieldErrors)
    {
        if (string.IsNullOrEmpty(orderPrice))
            return AddOrderFieldError<decimal>(orderFieldErrors, OrderFields.Price, OrderMessages.PriceRequired);

        if (!decimal.TryParse(orderPrice, OrderFieldJsonNumberStyle, CultureInfo.InvariantCulture, out var parsedOrderPrice))
        {
            if (IsOrderFieldNumberBeyondDecimal(orderPrice, out var isNegativeOrderFieldNumber))
                return AddOrderFieldError<decimal>(orderFieldErrors, OrderFields.Price,
                    isNegativeOrderFieldNumber ? OrderMessages.PriceNotPositive : OrderMessages.PriceTooLarge);

            return AddOrderFieldError<decimal>(orderFieldErrors, OrderFields.Price, OrderMessages.PriceNotNumber);
        }

        var checkedOrderPrice = CheckOrderPrice(parsedOrderPrice, orderFieldErrors);

        if (checkedOrderPrice is not null && SignificantDecimalPlacesInOrderFieldText(orderPrice) > OrderRules.PriceTick.Scale)
            return AddOrderFieldError<decimal>(orderFieldErrors, OrderFields.Price, OrderMessages.PriceOffTick);

        return checkedOrderPrice;
    }

    private static decimal? CheckOrderPrice(decimal orderPrice, List<OrderFieldError> orderFieldErrors)
    {
        if (orderPrice <= 0)
            return AddOrderFieldError<decimal>(orderFieldErrors, OrderFields.Price, OrderMessages.PriceNotPositive);

        if (orderPrice >= OrderRules.MaxPriceExclusive)
            return AddOrderFieldError<decimal>(orderFieldErrors, OrderFields.Price, OrderMessages.PriceTooLarge);

        // decimal é exato na base 10, então o resto da divisão diz se está no passo de 0,01.
        if (orderPrice % OrderRules.PriceTick != 0)
            return AddOrderFieldError<decimal>(orderFieldErrors, OrderFields.Price, OrderMessages.PriceOffTick);

        return orderPrice;
    }

    // Casas decimais que contam (sem zeros à direita), lidas no texto: o decimal arredonda o que
    // passa de 28 dígitos e faria "10.0000000000000000000000000001" virar 10.
    private static int SignificantDecimalPlacesInOrderFieldText(string orderFieldText)
    {
        var decimalSeparatorPosition = orderFieldText.IndexOf('.');
        return decimalSeparatorPosition < 0 ? 0 : orderFieldText[(decimalSeparatorPosition + 1)..].TrimEnd('0').Length;
    }

    private static bool IsOrderFieldNumberBeyondDecimal(string orderFieldText, out bool isNegativeOrderFieldNumber)
    {
        var isOrderFieldNumber = double.TryParse(orderFieldText, OrderFieldJsonNumberStyle, CultureInfo.InvariantCulture, out var approximateOrderFieldValue);
        isNegativeOrderFieldNumber = approximateOrderFieldValue < 0;
        return isOrderFieldNumber;
    }

    private static T? AddOrderFieldError<T>(List<OrderFieldError> orderFieldErrors, string orderField, string orderFieldErrorMessage) where T : struct
    {
        orderFieldErrors.Add(new OrderFieldError(orderField, orderFieldErrorMessage));
        return null;
    }
}
