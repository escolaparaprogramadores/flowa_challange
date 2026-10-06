using Flowa.OrderGenerator.Application.ErrorHandling;
using Flowa.OrderGenerator.Application.Orders.Interfaces;
using Flowa.OrderGenerator.Application.Orders.Responses;
using Flowa.Commons.Logging;
using Flowa.Commons.Observability;
using Flowa.Commons.Responses;
using Flowa.OrderGenerator.Domain.Orders.ValueObjects;

namespace Flowa.OrderGenerator.Application.Orders.UseCases;

public sealed class ListOrdersUseCase
{
    public const string OperationName = "orders.list-orders";
    public const string OrdersPageReadMessage = "Página de ordens lida.";
    public const string InvalidPageMessage = "Página inválida.";
    public const string InvalidPageErrorCode = "invalid-page";

    public static readonly string InvalidPageError =
        $"A página deve ser um número inteiro de {OrderListPagePolicy.FirstPageNumber} a {OrderListPagePolicy.MaxPageNumber}.";

    private readonly IStoredOrderRepository _storedOrderRepository;
    private readonly IOperationMonitoring _operationMonitoring;
    private readonly IApplicationLogger<ListOrdersUseCase> _logger;

    public ListOrdersUseCase(IStoredOrderRepository storedOrderRepository, IOperationMonitoring operationMonitoring, IApplicationLogger<ListOrdersUseCase> logger)
    {
        _storedOrderRepository = storedOrderRepository ?? throw new ArgumentNullException(nameof(storedOrderRepository));
        _operationMonitoring = operationMonitoring ?? throw new ArgumentNullException(nameof(operationMonitoring));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<DataMessage<OrderPageResponse>> ListOrdersAsync(string? requestedPageNumber, CancellationToken cancellationToken)
    {
        using var ordersListing = _operationMonitoring.StartOperationMonitoring(OperationName);
        try
        {
            if (!OrderListPagePolicy.TryReadOrderListPageNumber(requestedPageNumber, out var orderListPageNumber))
            {
                ordersListing.RecordOperationResult(OperationResults.InvalidInput);
                return DataMessage<OrderPageResponse>.CreateErrorMessage(InvalidPageMessage, ResultStatus.InvalidInput, [InvalidPageError], InvalidPageErrorCode);
            }

            var storedOrderPage = await _storedOrderRepository.OrderTableExistsAsync(cancellationToken)
                ? await _storedOrderRepository.ReadStoredOrderPageAsync(orderListPageNumber, cancellationToken)
                : StoredOrderPageResponse.CreateEmptyStoredOrderPage();

            ordersListing.RecordOperationResult(OperationResults.Succeeded);
            _logger.LogInformation("Stored orders page read.");
            return DataMessage<OrderPageResponse>.CreateSuccessMessage(
                OrderPageResponse.MapFromStoredOrderPage(orderListPageNumber, storedOrderPage), OrdersPageReadMessage);
        }
        catch (Exception ordersListingFailure)
        {
            if (UseCaseFailureDataMessageMapper.WasCancelledByTheCaller(ordersListingFailure, cancellationToken))
            {
                ordersListing.RecordOperationResult(OperationResults.CancelledByTheCaller);
                throw;
            }

            ordersListing.RecordOperationResult(OperationResults.Failed);
            return UseCaseFailureDataMessageMapper.MapFailureToDataMessage<OrderPageResponse>(ordersListingFailure);
        }
    }
}
