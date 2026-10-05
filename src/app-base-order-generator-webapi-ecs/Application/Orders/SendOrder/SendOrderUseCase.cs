using Base.OrderGenerator.Domain.Orders;
using Flowa.Shared;

namespace Base.OrderGenerator.Application.Orders.SendOrder;

// Sends an order that already passed the field check to the OrderAccumulator and returns its answer.
public sealed class SendOrderUseCase(IOrderAccumulatorPort orderAccumulatorPort)
{
    public Task<SentOrderResult> SendOrderAsync(ValidOrder order) => orderAccumulatorPort.SendOrderAsync(order);
}
