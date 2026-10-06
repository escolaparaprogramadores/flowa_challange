namespace Base.OrderGenerator.Domain.Orders.ValueObjects;

public sealed record OrderFieldFormatError
{
    public OrderFieldFormatError(string orderField, string orderFieldFormatMessage)
    {
        ArgumentException.ThrowIfNullOrEmpty(orderField);
        ArgumentException.ThrowIfNullOrEmpty(orderFieldFormatMessage);

        OrderField = orderField;
        OrderFieldFormatMessage = orderFieldFormatMessage;
    }

    public string OrderField { get; }
    public string OrderFieldFormatMessage { get; }
}
