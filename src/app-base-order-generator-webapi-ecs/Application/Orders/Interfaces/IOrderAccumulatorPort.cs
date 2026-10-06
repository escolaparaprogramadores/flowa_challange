using Base.OrderGenerator.Domain.Orders.ValueObjects;

namespace Base.OrderGenerator.Application.Orders.Interfaces;

public interface IOrderAccumulatorPort
{
    Task<SentOrderResult> SendOrderAsync(OrderToSend orderToSend);
}
