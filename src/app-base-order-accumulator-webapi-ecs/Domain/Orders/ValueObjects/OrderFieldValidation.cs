namespace Base.OrderAccumulator.Domain.Orders.ValueObjects;

public sealed record OrderFieldValidation(ValidOrderFields? ValidOrderFields, IReadOnlyList<string> InvalidOrderFieldMessages);
