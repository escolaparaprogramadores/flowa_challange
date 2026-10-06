namespace Base.OrderAccumulator.Domain.Orders;

// Field rule of the challenge. The backend keeps it only here (decision 12); an order that breaks it
// is an expected business answer, rejected with the reasons in tag 58 (decision 13).
public static class OrderFieldRule
{
    public static readonly IReadOnlyList<string> AllowedOrderSymbols = ["PETR4", "VALE3", "VIIA4"];

    public const int MaxOrderQuantityExclusive = 100_000;
    public const decimal MaxOrderPriceExclusive = 1_000m;
    public const decimal OrderPriceTick = 0.01m;

    public static OrderFieldValidation ValidateIncomingOrderFields(IncomingOrder incomingOrder)
    {
        var invalidOrderFieldMessages = new List<string>();

        var validOrderSymbol = CheckOrderSymbol(incomingOrder.Symbol, invalidOrderFieldMessages);
        var validOrderSide = CheckOrderSide(incomingOrder.Side, invalidOrderFieldMessages);
        var validOrderQuantity = CheckOrderQuantity(incomingOrder.Quantity, invalidOrderFieldMessages);
        var validOrderPrice = CheckOrderPrice(incomingOrder.Price, invalidOrderFieldMessages);

        if (invalidOrderFieldMessages.Count > 0)
            return new OrderFieldValidation(null, invalidOrderFieldMessages);

        return new OrderFieldValidation(
            new ValidOrderFields(validOrderSymbol!, validOrderSide!.Value, validOrderQuantity!.Value, validOrderPrice!.Value),
            invalidOrderFieldMessages);
    }

    private static string? CheckOrderSymbol(string? orderSymbol, List<string> invalidOrderFieldMessages)
    {
        if (string.IsNullOrEmpty(orderSymbol))
        {
            invalidOrderFieldMessages.Add(OrderFieldMessages.OrderSymbolRequiredMessage);
            return null;
        }

        if (!AllowedOrderSymbols.Contains(orderSymbol))
        {
            invalidOrderFieldMessages.Add(OrderFieldMessages.OrderSymbolInvalidMessage);
            return null;
        }

        return orderSymbol;
    }

    private static OrderSide? CheckOrderSide(char orderSide, List<string> invalidOrderFieldMessages) => orderSide switch
    {
        OrderSideCodes.BuyOrderSideFixCode => OrderSide.Buy,
        OrderSideCodes.SellOrderSideFixCode => OrderSide.Sell,
        _ => AddInvalidOrderFieldMessage<OrderSide>(invalidOrderFieldMessages, OrderFieldMessages.OrderSideInvalidMessage)
    };

    private static int? CheckOrderQuantity(decimal orderQuantity, List<string> invalidOrderFieldMessages)
    {
        if (decimal.Truncate(orderQuantity) != orderQuantity)
            return AddInvalidOrderFieldMessage<int>(invalidOrderFieldMessages, OrderFieldMessages.OrderQuantityNotIntegerMessage);

        if (orderQuantity <= 0)
            return AddInvalidOrderFieldMessage<int>(invalidOrderFieldMessages, OrderFieldMessages.OrderQuantityNotPositiveMessage);

        if (orderQuantity >= MaxOrderQuantityExclusive)
            return AddInvalidOrderFieldMessage<int>(invalidOrderFieldMessages, OrderFieldMessages.OrderQuantityTooLargeMessage);

        return (int)orderQuantity;
    }

    private static decimal? CheckOrderPrice(decimal orderPrice, List<string> invalidOrderFieldMessages)
    {
        if (orderPrice <= 0)
            return AddInvalidOrderFieldMessage<decimal>(invalidOrderFieldMessages, OrderFieldMessages.OrderPriceNotPositiveMessage);

        if (orderPrice >= MaxOrderPriceExclusive)
            return AddInvalidOrderFieldMessage<decimal>(invalidOrderFieldMessages, OrderFieldMessages.OrderPriceTooLargeMessage);

        // decimal is exact in base 10, so the remainder tells whether the price sits on the 0.01 step.
        if (orderPrice % OrderPriceTick != 0)
            return AddInvalidOrderFieldMessage<decimal>(invalidOrderFieldMessages, OrderFieldMessages.OrderPriceOffTickMessage);

        return orderPrice;
    }

    private static TOrderFieldValue? AddInvalidOrderFieldMessage<TOrderFieldValue>(List<string> invalidOrderFieldMessages, string invalidOrderFieldMessage)
        where TOrderFieldValue : struct
    {
        invalidOrderFieldMessages.Add(invalidOrderFieldMessage);
        return null;
    }
}
