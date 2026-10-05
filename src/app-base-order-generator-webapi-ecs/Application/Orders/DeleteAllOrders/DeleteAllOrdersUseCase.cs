using Base.OrderGenerator.Commons;

namespace Base.OrderGenerator.Application.Orders.DeleteAllOrders;

// "Delete all" is done by the OrderAccumulator; the answer has no body (204).
public sealed class DeleteAllOrdersUseCase(IOrderAccumulatorHttpClient orderAccumulatorHttpClient)
{
    public Task DeleteAllOrdersAsync(CancellationToken cancellationToken) =>
        orderAccumulatorHttpClient.DeleteAllStoredOrdersAsync(cancellationToken);
}
