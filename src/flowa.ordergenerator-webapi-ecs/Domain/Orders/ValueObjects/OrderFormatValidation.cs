namespace Flowa.OrderGenerator.Domain.Orders.ValueObjects;

public sealed record OrderFormatValidation
{
    public OrderFormatValidation(OrderToSend? orderToSend, IReadOnlyList<OrderFieldFormatError> orderFieldFormatErrors)
    {
        ArgumentNullException.ThrowIfNull(orderFieldFormatErrors);

        OrderToSend = orderToSend;
        OrderFieldFormatErrors = orderFieldFormatErrors;
    }

    public OrderToSend? OrderToSend { get; }
    public IReadOnlyList<OrderFieldFormatError> OrderFieldFormatErrors { get; }

    public IReadOnlyList<string> ListOrderFieldFormatMessages() =>
        [.. OrderFieldFormatErrors.Select(orderFieldFormatError => orderFieldFormatError.OrderFieldFormatMessage)];
}
