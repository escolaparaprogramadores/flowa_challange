using Base.OrderGenerator.Domain.Orders;
using Flowa.Shared;

namespace Base.OrderGenerator.Application.Orders.SendOrder;

// Sends the order to the OrderAccumulator and waits for its answer.
public interface IOrderAccumulatorPort
{
    Task<SentOrderResult> SendOrderAsync(ValidOrder order);
}
