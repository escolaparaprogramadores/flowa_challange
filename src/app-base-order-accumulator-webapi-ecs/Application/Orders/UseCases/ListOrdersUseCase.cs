using Base.OrderAccumulator.Application.ErrorHandling;
using Base.OrderAccumulator.Application.Orders.Interfaces;
using Base.OrderAccumulator.Application.Orders.Responses;
using Base.OrderAccumulator.Commons.Responses;

namespace Base.OrderAccumulator.Application.Orders.UseCases;

public sealed class ListOrdersUseCase
{
    public const string OrdersPageReadMessage = "Página de ordens lida.";

    private readonly IOrderListReadRepository orderListReadRepository;

    public ListOrdersUseCase(IOrderListReadRepository orderListReadRepository)
    {
        this.orderListReadRepository = orderListReadRepository ?? throw new ArgumentNullException(nameof(orderListReadRepository));
    }

    public async Task<DataMessage<StoredOrderPageResponse>> ListOrdersAsync(int pageNumber, CancellationToken cancellationToken = default)
    {
        try
        {
            return DataMessage<StoredOrderPageResponse>.CreateSuccessMessage(
                await orderListReadRepository.ReadStoredOrderPageAsync(pageNumber, cancellationToken), OrdersPageReadMessage);
        }
        catch (Exception orderListFailure)
        {
            return UseCaseFailureDataMessageMapper.MapUseCaseFailureToDataMessage<StoredOrderPageResponse>(orderListFailure);
        }
    }
}
