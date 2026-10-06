using System.Text.Json;
using Base.OrderGenerator.Application.ErrorHandling;
using Base.OrderGenerator.Application.Orders.Interfaces;
using Base.OrderGenerator.Commons.Logging;
using Base.OrderGenerator.Commons.Observability;
using Base.OrderGenerator.Commons.Responses;

namespace Base.OrderGenerator.Application.Orders.UseCases;

public sealed class ListOrdersUseCase
{
    public const string OperationName = "orders.list-orders";

    private readonly IStoredOrdersPort _storedOrdersPort;
    private readonly IOperationMonitoring _operationMonitoring;
    private readonly IApplicationLogger<ListOrdersUseCase> _logger;

    public ListOrdersUseCase(IStoredOrdersPort storedOrdersPort, IOperationMonitoring operationMonitoring, IApplicationLogger<ListOrdersUseCase> logger)
    {
        _storedOrdersPort = storedOrdersPort ?? throw new ArgumentNullException(nameof(storedOrdersPort));
        _operationMonitoring = operationMonitoring ?? throw new ArgumentNullException(nameof(operationMonitoring));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<DataMessage<JsonElement>> ListOrdersAsync(string? requestedPageNumber, CancellationToken cancellationToken)
    {
        using var ordersListing = _operationMonitoring.StartOperationMonitoring(OperationName);
        try
        {
            var storedOrdersPage = await _storedOrdersPort.GetStoredOrdersPageAsync(requestedPageNumber, cancellationToken);
            if (!storedOrdersPage.Success)
            {
                ordersListing.RecordOperationResult(OperationResults.InvalidInput);
                return storedOrdersPage;
            }

            ordersListing.RecordOperationResult(OperationResults.Succeeded);
            _logger.LogInformation("Stored orders page read.", new { RequestedPageNumber = requestedPageNumber });
            return storedOrdersPage;
        }
        catch (Exception ordersListingFailure)
        {
            if (UseCaseFailureDataMessageMapper.WasCancelledByTheCaller(ordersListingFailure, cancellationToken))
            {
                ordersListing.RecordOperationResult(OperationResults.CancelledByTheCaller);
                throw;
            }

            ordersListing.RecordOperationResult(OperationResults.Failed);
            return UseCaseFailureDataMessageMapper.MapFailureToDataMessage<JsonElement>(ordersListingFailure);
        }
    }
}
