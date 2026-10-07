using Flowa.OrderGenerator.Domain.Orders.ValueObjects;

namespace Flowa.OrderGenerator.Application.Orders.Interfaces;

public interface IOrderAccumulatorPort
{
    Task<SentOrderResult> SendOrderAsync(OrderToSend orderToSend);
}
