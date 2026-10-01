using System.Globalization;
using System.Text.Json.Serialization;

namespace Flowa.Shared;

// O erro vai direto na resposta HTTP; o contrato promete os campos "field" e "message".
public sealed record OrderFieldError(
    [property: JsonPropertyName("field")] string OrderField,
    [property: JsonPropertyName("message")] string OrderFieldErrorMessage);

public sealed record ValidOrder(string OrderSymbol, OrderSide OrderSide, int OrderQuantity, decimal OrderPrice);

public sealed record OrderValidationResult(ValidOrder? ValidatedOrder, IReadOnlyList<OrderFieldError> OrderFieldErrors)
{
    public bool IsOrderValid => OrderFieldErrors.Count == 0;
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
            orderFieldErrors.Add(new OrderFieldError(OrderFields.OrderSymbolFieldName, OrderMessages.OrderSymbolRequiredMessage));
            return null;
        }

        if (!OrderRules.AllowedOrderSymbols.Contains(orderSymbol))
        {
            orderFieldErrors.Add(new OrderFieldError(OrderFields.OrderSymbolFieldName, OrderMessages.OrderSymbolInvalidMessage));
            return null;
        }

        return orderSymbol;
    }

    private static OrderSide? ParseJsonOrderSide(string? orderSide, List<OrderFieldError> orderFieldErrors) => orderSide switch
    {
        OrderSideCodes.BuyOrderSideJsonCode => OrderSide.Buy,
        OrderSideCodes.SellOrderSideJsonCode => OrderSide.Sell,
        null or "" => AddOrderFieldError<OrderSide>(orderFieldErrors, OrderFields.OrderSideFieldName, OrderMessages.OrderSideRequiredMessage),
        _ => AddOrderFieldError<OrderSide>(orderFieldErrors, OrderFields.OrderSideFieldName, OrderMessages.OrderSideInvalidMessage)
    };

    private static OrderSide? ParseFixOrderSide(char orderSide, List<OrderFieldError> orderFieldErrors) => orderSide switch
    {
        OrderSideCodes.BuyOrderSideFixCode => OrderSide.Buy,
        OrderSideCodes.SellOrderSideFixCode => OrderSide.Sell,
        _ => AddOrderFieldError<OrderSide>(orderFieldErrors, OrderFields.OrderSideFieldName, OrderMessages.OrderSideInvalidMessage)
    };

    private static int? ParseOrderQuantity(string? orderQuantity, List<OrderFieldError> orderFieldErrors)
    {
        if (string.IsNullOrEmpty(orderQuantity))
            return AddOrderFieldError<int>(orderFieldErrors, OrderFields.OrderQuantityFieldName, OrderMessages.OrderQuantityRequiredMessage);

        if (!decimal.TryParse(orderQuantity, OrderFieldJsonNumberStyle, CultureInfo.InvariantCulture, out var parsedOrderQuantity))
        {
            // Número que nem cabe no decimal é grande demais, não "texto".
            if (IsOrderFieldNumberBeyondDecimal(orderQuantity, out var isNegativeOrderFieldNumber))
                return AddOrderFieldError<int>(orderFieldErrors, OrderFields.OrderQuantityFieldName,
                    isNegativeOrderFieldNumber ? OrderMessages.OrderQuantityNotPositiveMessage : OrderMessages.OrderQuantityTooLargeMessage);

            return AddOrderFieldError<int>(orderFieldErrors, OrderFields.OrderQuantityFieldName, OrderMessages.OrderQuantityNotIntegerMessage);
        }

        if (SignificantDecimalPlacesInOrderFieldText(orderQuantity) > 0)
            return AddOrderFieldError<int>(orderFieldErrors, OrderFields.OrderQuantityFieldName, OrderMessages.OrderQuantityNotIntegerMessage);

        return CheckOrderQuantity(parsedOrderQuantity, orderFieldErrors);
    }

    private static int? CheckOrderQuantity(decimal orderQuantity, List<OrderFieldError> orderFieldErrors)
    {
        if (decimal.Truncate(orderQuantity) != orderQuantity)
            return AddOrderFieldError<int>(orderFieldErrors, OrderFields.OrderQuantityFieldName, OrderMessages.OrderQuantityNotIntegerMessage);

        if (orderQuantity <= 0)
            return AddOrderFieldError<int>(orderFieldErrors, OrderFields.OrderQuantityFieldName, OrderMessages.OrderQuantityNotPositiveMessage);

        if (orderQuantity >= OrderRules.MaxOrderQuantityExclusive)
            return AddOrderFieldError<int>(orderFieldErrors, OrderFields.OrderQuantityFieldName, OrderMessages.OrderQuantityTooLargeMessage);

        return (int)orderQuantity;
    }

    private static decimal? ParseOrderPrice(string? orderPrice, List<OrderFieldError> orderFieldErrors)
    {
        if (string.IsNullOrEmpty(orderPrice))
            return AddOrderFieldError<decimal>(orderFieldErrors, OrderFields.OrderPriceFieldName, OrderMessages.OrderPriceRequiredMessage);

        if (!decimal.TryParse(orderPrice, OrderFieldJsonNumberStyle, CultureInfo.InvariantCulture, out var parsedOrderPrice))
        {
            if (IsOrderFieldNumberBeyondDecimal(orderPrice, out var isNegativeOrderFieldNumber))
                return AddOrderFieldError<decimal>(orderFieldErrors, OrderFields.OrderPriceFieldName,
                    isNegativeOrderFieldNumber ? OrderMessages.OrderPriceNotPositiveMessage : OrderMessages.OrderPriceTooLargeMessage);

            return AddOrderFieldError<decimal>(orderFieldErrors, OrderFields.OrderPriceFieldName, OrderMessages.OrderPriceNotNumberMessage);
        }

        var checkedOrderPrice = CheckOrderPrice(parsedOrderPrice, orderFieldErrors);

        if (checkedOrderPrice is not null && SignificantDecimalPlacesInOrderFieldText(orderPrice) > OrderRules.OrderPriceTick.Scale)
            return AddOrderFieldError<decimal>(orderFieldErrors, OrderFields.OrderPriceFieldName, OrderMessages.OrderPriceOffTickMessage);

        return checkedOrderPrice;
    }

    private static decimal? CheckOrderPrice(decimal orderPrice, List<OrderFieldError> orderFieldErrors)
    {
        if (orderPrice <= 0)
            return AddOrderFieldError<decimal>(orderFieldErrors, OrderFields.OrderPriceFieldName, OrderMessages.OrderPriceNotPositiveMessage);

        if (orderPrice >= OrderRules.MaxOrderPriceExclusive)
            return AddOrderFieldError<decimal>(orderFieldErrors, OrderFields.OrderPriceFieldName, OrderMessages.OrderPriceTooLargeMessage);

        // decimal é exato na base 10, então o resto da divisão diz se está no passo de 0,01.
        if (orderPrice % OrderRules.OrderPriceTick != 0)
            return AddOrderFieldError<decimal>(orderFieldErrors, OrderFields.OrderPriceFieldName, OrderMessages.OrderPriceOffTickMessage);

        return orderPrice;
    }

    // Casas decimais que contam (sem zeros à direita), lidas no texto: o decimal arredonda o que
    // passa de 28 dígitos e faria "10.0000000000000000000000000001" virar 10.
    private static int SignificantDecimalPlacesInOrderFieldText(string orderFieldText)
    {
        var orderFieldDecimalSeparatorPosition = orderFieldText.IndexOf('.');
        return orderFieldDecimalSeparatorPosition < 0 ? 0 : orderFieldText[(orderFieldDecimalSeparatorPosition + 1)..].TrimEnd('0').Length;
    }

    private static bool IsOrderFieldNumberBeyondDecimal(string orderFieldText, out bool isNegativeOrderFieldNumber)
    {
        var isOrderFieldNumber = double.TryParse(orderFieldText, OrderFieldJsonNumberStyle, CultureInfo.InvariantCulture, out var approximateOrderFieldValue);
        isNegativeOrderFieldNumber = approximateOrderFieldValue < 0;
        return isOrderFieldNumber;
    }

    private static TOrderFieldValue? AddOrderFieldError<TOrderFieldValue>(List<OrderFieldError> orderFieldErrors, string orderField, string orderFieldErrorMessage) where TOrderFieldValue : struct
    {
        orderFieldErrors.Add(new OrderFieldError(orderField, orderFieldErrorMessage));
        return null;
    }
}
