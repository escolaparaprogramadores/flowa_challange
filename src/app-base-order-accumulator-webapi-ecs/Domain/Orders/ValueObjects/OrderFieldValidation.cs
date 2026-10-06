namespace Base.OrderAccumulator.Domain.Orders.ValueObjects;

public sealed record OrderFieldValidation
{
    public ValidOrderFields? ValidOrderFields { get; }
    public IReadOnlyList<string> InvalidOrderFieldMessages { get; }

    public OrderFieldValidation(ValidOrderFields? validOrderFields, IReadOnlyList<string> invalidOrderFieldMessages)
    {
        ArgumentNullException.ThrowIfNull(invalidOrderFieldMessages);

        ValidOrderFields = validOrderFields;
        InvalidOrderFieldMessages = invalidOrderFieldMessages;
    }
}
