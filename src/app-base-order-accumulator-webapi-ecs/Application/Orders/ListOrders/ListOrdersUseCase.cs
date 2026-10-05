using Base.OrderAccumulator.Commons;

namespace Base.OrderAccumulator.Application.Orders.ListOrders;

// The order list of the screen, newest first, one page at a time.
public sealed class ListOrdersUseCase(IOrderListReadRepository orderListReadRepository)
{
    public const string OrdersPageReadMessage = "Página de ordens lida.";

    public async Task<DataMessage<OrderListPage>> ListOrdersAsync(int pageNumber, CancellationToken cancellationToken = default) =>
        DataMessage<OrderListPage>.CreateSuccessMessage(
            await orderListReadRepository.ReadStoredOrderPageAsync(pageNumber, cancellationToken), OrdersPageReadMessage);
}
