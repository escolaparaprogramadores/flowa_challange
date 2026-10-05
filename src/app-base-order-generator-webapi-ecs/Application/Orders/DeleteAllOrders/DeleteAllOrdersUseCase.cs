using Base.OrderGenerator.Commons;

namespace Base.OrderGenerator.Application.Orders.DeleteAllOrders;

// "Delete all" is done by the OrderAccumulator. An OrderAccumulator that does not answer throws, and the
// GlobalErrorHandler answers the 503.
public sealed class DeleteAllOrdersUseCase(IOrderAccumulatorHttpClient orderAccumulatorHttpClient)
{
    public const string AllOrdersDeletedMessage = "Todas as ordens foram apagadas.";

    public async Task<DataMessage<bool>> DeleteAllOrdersAsync(CancellationToken cancellationToken)
    {
        await orderAccumulatorHttpClient.DeleteAllStoredOrdersAsync(cancellationToken);
        return DataMessage<bool>.CreateSuccessMessage(true, AllOrdersDeletedMessage);
    }
}
