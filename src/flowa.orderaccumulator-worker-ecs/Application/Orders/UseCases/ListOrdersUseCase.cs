using Flowa.OrderAccumulator.Application.ErrorHandling;
using Flowa.OrderAccumulator.Application.Orders.Interfaces;
using Flowa.OrderAccumulator.Application.Orders.Responses;
using Flowa.Commons.Logging;
using Flowa.Commons.Observability;
using Flowa.Commons.Responses;

namespace Flowa.OrderAccumulator.Application.Orders.UseCases;

public sealed class ListOrdersUseCase
{
    public const string OrdersPageReadMessage = "Página de ordens lida.";
    public const string OperationName = "orders.list-orders";

    private readonly IOrderListReadRepository orderListReadRepository;
    private readonly IOperationMonitoring operationMonitoring;
    private readonly IApplicationLogger<ListOrdersUseCase> orderListLogger;

    public ListOrdersUseCase(
        IOrderListReadRepository orderListReadRepository, IOperationMonitoring operationMonitoring, IApplicationLogger<ListOrdersUseCase> orderListLogger)
    {
        this.orderListReadRepository = orderListReadRepository ?? throw new ArgumentNullException(nameof(orderListReadRepository));
        this.operationMonitoring = operationMonitoring ?? throw new ArgumentNullException(nameof(operationMonitoring));
        this.orderListLogger = orderListLogger ?? throw new ArgumentNullException(nameof(orderListLogger));
    }

    public async Task<DataMessage<StoredOrderPageResponse>> ListOrdersAsync(int pageNumber, CancellationToken cancellationToken = default)
    {
        using var orderListMonitoring = operationMonitoring.StartOperationMonitoring(OperationName);
        try
        {
            var storedOrderPage = await orderListReadRepository.ReadStoredOrderPageAsync(pageNumber, cancellationToken);
            orderListLogger.LogInformation("Stored orders page read.", new
            {
                PageNumber = pageNumber,
                storedOrderPage.TotalStoredOrders,
                OrdersOnPage = storedOrderPage.StoredOrders.Count
            });
            return DataMessage<StoredOrderPageResponse>.CreateSuccessMessage(storedOrderPage, OrdersPageReadMessage);
        }
        catch (Exception orderListFailure)
        {
            orderListMonitoring.RecordOperationResult(OperationResults.Failed);
            return UseCaseFailureDataMessageMapper.MapUseCaseFailureToDataMessage<StoredOrderPageResponse>(orderListFailure);
        }
    }
}
