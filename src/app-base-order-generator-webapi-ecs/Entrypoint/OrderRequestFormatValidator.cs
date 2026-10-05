using System.Globalization;
using System.Text.Json.Serialization;
using Base.OrderGenerator.Domain.Orders;

namespace Base.OrderGenerator.Entrypoint;

// The error goes straight into the HTTP response; the contract promises the "field" and "message" names.
public sealed record OrderFieldFormatError(
    [property: JsonPropertyName("field")] string OrderField,
    [property: JsonPropertyName("message")] string OrderFieldFormatMessage);

public sealed record OrderRequestFormatValidation(OrderToSend? OrderToSend, IReadOnlyList<OrderFieldFormatError> OrderFieldFormatErrors);

// Checks only what a FIX NewOrderSingle cannot carry: a missing field, a number of the wrong type, an
// unknown side, a symbol with a control character. The field rule (symbols, limits, whole quantity,
// 0.01 step) belongs to the OrderAccumulator, which answers it through FIX (decisions 12 and 24).
public static class OrderRequestFormatValidator
{
    public const string OrderSymbolFieldName = "symbol";
    public const string OrderSideFieldName = "side";
    public const string OrderQuantityFieldName = "quantity";
    public const string OrderPriceFieldName = "price";

    public const string BuyOrderSideJsonCode = "buy";
    public const string SellOrderSideJsonCode = "sell";

    public const string OrderSymbolRequiredMessage = "Informe o símbolo.";
    public const string OrderSymbolControlCharacterMessage = "O símbolo não pode ter caractere de controle.";
    public const string OrderSideRequiredMessage = "Informe o lado da ordem.";
    public const string OrderSideInvalidMessage = "Lado inválido. Use compra ou venda.";
    public const string OrderQuantityRequiredMessage = "Informe a quantidade.";
    public const string OrderQuantityNotNumberMessage = "A quantidade deve ser um número inteiro.";
    public const string OrderPriceRequiredMessage = "Informe o preço.";
    public const string OrderPriceNotNumberMessage = "O preço deve ser um número.";

    // Dot as decimal separator and no thousands separator or exponent: the JSON number as it is written.
    private const NumberStyles OrderFieldJsonNumberStyle = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;

    // The fields arrive as raw JSON text, so "abc" or 1e3 get a message instead of a framework error.
    public static OrderRequestFormatValidation ValidateOrderRequestFormat(string? orderSymbol, string? orderSide, string? orderQuantity, string? orderPrice)
    {
        var orderFieldFormatErrors = new List<OrderFieldFormatError>();

        var formattedOrderSymbol = CheckOrderSymbolFormat(orderSymbol, orderFieldFormatErrors);
        var formattedOrderSide = ParseOrderSide(orderSide, orderFieldFormatErrors);
        var formattedOrderQuantity = ParseOrderNumber(orderQuantity, OrderQuantityFieldName,
            OrderQuantityRequiredMessage, OrderQuantityNotNumberMessage, orderFieldFormatErrors);
        var formattedOrderPrice = ParseOrderNumber(orderPrice, OrderPriceFieldName,
            OrderPriceRequiredMessage, OrderPriceNotNumberMessage, orderFieldFormatErrors);

        if (orderFieldFormatErrors.Count > 0)
            return new OrderRequestFormatValidation(null, orderFieldFormatErrors);

        return new OrderRequestFormatValidation(
            new OrderToSend(formattedOrderSymbol!, formattedOrderSide!.Value, formattedOrderQuantity!.Value, formattedOrderPrice!.Value),
            orderFieldFormatErrors);
    }

    public static string ToJsonOrderSide(OrderSide orderSide) => orderSide == OrderSide.Buy ? BuyOrderSideJsonCode : SellOrderSideJsonCode;

    // FIX separates fields with control character 0x01; a symbol carrying one would break the message.
    private static string? CheckOrderSymbolFormat(string? orderSymbol, List<OrderFieldFormatError> orderFieldFormatErrors)
    {
        if (string.IsNullOrEmpty(orderSymbol))
        {
            orderFieldFormatErrors.Add(new OrderFieldFormatError(OrderSymbolFieldName, OrderSymbolRequiredMessage));
            return null;
        }

        if (orderSymbol.Any(char.IsControl))
        {
            orderFieldFormatErrors.Add(new OrderFieldFormatError(OrderSymbolFieldName, OrderSymbolControlCharacterMessage));
            return null;
        }

        return orderSymbol;
    }

    private static OrderSide? ParseOrderSide(string? orderSide, List<OrderFieldFormatError> orderFieldFormatErrors) => orderSide switch
    {
        BuyOrderSideJsonCode => OrderSide.Buy,
        SellOrderSideJsonCode => OrderSide.Sell,
        null or "" => AddOrderFieldFormatError<OrderSide>(orderFieldFormatErrors, OrderSideFieldName, OrderSideRequiredMessage),
        _ => AddOrderFieldFormatError<OrderSide>(orderFieldFormatErrors, OrderSideFieldName, OrderSideInvalidMessage)
    };

    // A number the FIX decimal field cannot hold exactly is the wrong type: decimal would round it, and
    // a rounded price could pass the 0.01 step it does not meet.
    private static decimal? ParseOrderNumber(string? orderNumberText, string orderField, string requiredMessage, string notNumberMessage,
        List<OrderFieldFormatError> orderFieldFormatErrors)
    {
        if (string.IsNullOrEmpty(orderNumberText))
            return AddOrderFieldFormatError<decimal>(orderFieldFormatErrors, orderField, requiredMessage);

        if (!decimal.TryParse(orderNumberText, OrderFieldJsonNumberStyle, CultureInfo.InvariantCulture, out var parsedOrderNumber)
            || CountSignificantDecimalPlaces(orderNumberText) != CountSignificantDecimalPlaces(parsedOrderNumber.ToString(CultureInfo.InvariantCulture)))
            return AddOrderFieldFormatError<decimal>(orderFieldFormatErrors, orderField, notNumberMessage);

        return parsedOrderNumber;
    }

    private static int CountSignificantDecimalPlaces(string orderNumberText)
    {
        var decimalSeparatorPosition = orderNumberText.IndexOf('.');
        return decimalSeparatorPosition < 0 ? 0 : orderNumberText[(decimalSeparatorPosition + 1)..].TrimEnd('0').Length;
    }

    private static TOrderFieldValue? AddOrderFieldFormatError<TOrderFieldValue>(
        List<OrderFieldFormatError> orderFieldFormatErrors, string orderField, string orderFieldFormatMessage) where TOrderFieldValue : struct
    {
        orderFieldFormatErrors.Add(new OrderFieldFormatError(orderField, orderFieldFormatMessage));
        return null;
    }
}
