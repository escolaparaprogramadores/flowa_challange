using Base.OrderGenerator.Domain.Orders.Enums;

namespace Base.OrderGenerator.Domain.Orders.ValueObjects;

public sealed record OrderToSend(string Symbol, OrderSide Side, decimal Quantity, decimal Price);
