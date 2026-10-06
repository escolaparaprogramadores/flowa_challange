using Flowa.OrderGenerator.Application.Orders.Commands;

namespace Flowa.OrderGenerator.Entrypoint.Orders.Requests;

public sealed record SendOrderRequest(string? Symbol, string? Side, string? Quantity, string? Price)
{
    public SendOrderCommand MapToSendOrderCommand() => new(Symbol, Side, Quantity, Price);
}
