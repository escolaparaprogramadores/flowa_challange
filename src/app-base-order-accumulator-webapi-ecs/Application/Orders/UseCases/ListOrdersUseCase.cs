using Base.OrderAccumulator.Application.Orders.Interfaces;
using Base.OrderAccumulator.Application.Orders.Responses;
using Base.OrderAccumulator.Commons.Responses;

namespace Base.OrderAccumulator.Application.Orders.UseCases;

public sealed class ListOrdersUseCase(IOrderListReadRepository orderListReadRepository)
{
    public const string OrdersPageReadMessage = "Página de ordens lida.";

    public async Task<DataMessage<StoredOrderPageResponse>> ListOrdersAsync(int pageNumber, CancellationToken cancellationToken = default) =>
        DataMessage<StoredOrderPageResponse>.CreateSuccessMessage(
            await orderListReadRepository.ReadStoredOrderPageAsync(pageNumber, cancellationToken), OrdersPageReadMessage);
}
