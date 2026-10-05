using Base.OrderGenerator.Domain.Orders;

namespace Base.OrderGenerator.Application.Orders.SendOrder;

// Sends the order to the OrderAccumulator and waits for its answer.
public interface IOrderAccumulatorPort
{
    Task<SentOrderResult> SendOrderAsync(OrderToSend orderToSend);
}
