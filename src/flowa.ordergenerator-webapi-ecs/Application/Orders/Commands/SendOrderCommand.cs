namespace Flowa.OrderGenerator.Application.Orders.Commands;

public sealed record SendOrderCommand(string? Symbol, string? Side, string? Quantity, string? Price);
