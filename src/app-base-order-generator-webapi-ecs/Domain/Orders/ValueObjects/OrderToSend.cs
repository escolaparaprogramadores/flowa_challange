using System.Globalization;
using Base.OrderGenerator.Domain.Orders.Enums;

namespace Base.OrderGenerator.Domain.Orders.ValueObjects;

public sealed record OrderToSend
{
    public const string OrderSymbolFieldName = "symbol";
    public const string OrderSideFieldName = "side";
    public const string OrderQuantityFieldName = "quantity";
    public const string OrderPriceFieldName = "price";

    public const string BuyOrderSideCode = "buy";
    public const string SellOrderSideCode = "sell";

    public const string OrderSymbolRequiredMessage = "Informe o símbolo.";
    public const string OrderSymbolControlCharacterMessage = "O símbolo não pode ter caractere de controle.";
    public const string OrderSideRequiredMessage = "Informe o lado da ordem.";
    public const string OrderSideInvalidMessage = "Lado inválido. Use compra ou venda.";
    public const string OrderQuantityRequiredMessage = "Informe a quantidade.";
    public const string OrderQuantityNotNumberMessage = "A quantidade deve ser um número inteiro.";
    public const string OrderPriceRequiredMessage = "Informe o preço.";
    public const string OrderPriceNotNumberMessage = "O preço deve ser um número.";

    private const NumberStyles OrderFieldNumberStyle = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;

    public OrderToSend(string symbol, OrderSide side, decimal quantity, decimal price)
    {
        ArgumentException.ThrowIfNullOrEmpty(symbol);
        if (HasControlCharacter(symbol))
            throw new ArgumentException(OrderSymbolControlCharacterMessage, nameof(symbol));
        if (!Enum.IsDefined(side))
            throw new ArgumentOutOfRangeException(nameof(side), side, OrderSideInvalidMessage);

        Symbol = symbol;
        Side = side;
        Quantity = quantity;
        Price = price;
    }

    public string Symbol { get; }
    public OrderSide Side { get; }
    public decimal Quantity { get; }
    public decimal Price { get; }

    public string DescribeOrderSideCode() => Side == OrderSide.Buy ? BuyOrderSideCode : SellOrderSideCode;

    public static OrderFormatValidation ValidateOrderFormat(string? orderSymbol, string? orderSide, string? orderQuantity, string? orderPrice)
    {
        var orderFieldFormatErrors = new List<OrderFieldFormatError>();

        var checkedOrderSymbol = CheckOrderSymbolFormat(orderSymbol, orderFieldFormatErrors);
        var parsedOrderSide = ParseOrderSide(orderSide, orderFieldFormatErrors);
        var parsedOrderQuantity = ParseOrderNumber(orderQuantity, OrderQuantityFieldName,
            OrderQuantityRequiredMessage, OrderQuantityNotNumberMessage, orderFieldFormatErrors);
        var parsedOrderPrice = ParseOrderNumber(orderPrice, OrderPriceFieldName,
            OrderPriceRequiredMessage, OrderPriceNotNumberMessage, orderFieldFormatErrors);

        if (orderFieldFormatErrors.Count > 0)
            return new OrderFormatValidation(null, orderFieldFormatErrors);

        return new OrderFormatValidation(
            new OrderToSend(checkedOrderSymbol!, parsedOrderSide!.Value, parsedOrderQuantity!.Value, parsedOrderPrice!.Value),
            orderFieldFormatErrors);
    }

    private static bool HasControlCharacter(string orderSymbol) => orderSymbol.Any(char.IsControl);

    private static string? CheckOrderSymbolFormat(string? orderSymbol, List<OrderFieldFormatError> orderFieldFormatErrors)
    {
        if (string.IsNullOrEmpty(orderSymbol))
        {
            orderFieldFormatErrors.Add(new OrderFieldFormatError(OrderSymbolFieldName, OrderSymbolRequiredMessage));
            return null;
        }

        if (HasControlCharacter(orderSymbol))
        {
            orderFieldFormatErrors.Add(new OrderFieldFormatError(OrderSymbolFieldName, OrderSymbolControlCharacterMessage));
            return null;
        }

        return orderSymbol;
    }

    private static OrderSide? ParseOrderSide(string? orderSide, List<OrderFieldFormatError> orderFieldFormatErrors) => orderSide switch
    {
        BuyOrderSideCode => OrderSide.Buy,
        SellOrderSideCode => OrderSide.Sell,
        null or "" => AddOrderFieldFormatError<OrderSide>(orderFieldFormatErrors, OrderSideFieldName, OrderSideRequiredMessage),
        _ => AddOrderFieldFormatError<OrderSide>(orderFieldFormatErrors, OrderSideFieldName, OrderSideInvalidMessage)
    };

    private static decimal? ParseOrderNumber(string? orderNumberText, string orderField, string requiredMessage, string notNumberMessage,
        List<OrderFieldFormatError> orderFieldFormatErrors)
    {
        if (string.IsNullOrEmpty(orderNumberText))
            return AddOrderFieldFormatError<decimal>(orderFieldFormatErrors, orderField, requiredMessage);

        if (!decimal.TryParse(orderNumberText, OrderFieldNumberStyle, CultureInfo.InvariantCulture, out var parsedOrderNumber)
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
