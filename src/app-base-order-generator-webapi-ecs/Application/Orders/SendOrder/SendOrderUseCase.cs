using Base.OrderGenerator.Domain.Orders;

namespace Base.OrderGenerator.Application.Orders.SendOrder;

// Sends an order whose format was checked to the OrderAccumulator and returns its answer.
public sealed class SendOrderUseCase(IOrderAccumulatorPort orderAccumulatorPort)
{
    public Task<SentOrderResult> SendOrderAsync(OrderToSend orderToSend) => orderAccumulatorPort.SendOrderAsync(orderToSend);
}
