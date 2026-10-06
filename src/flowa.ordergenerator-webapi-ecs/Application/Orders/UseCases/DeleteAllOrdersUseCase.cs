using Flowa.OrderGenerator.Application.ErrorHandling;
using Flowa.OrderGenerator.Application.Orders.Interfaces;
using Flowa.OrderGenerator.Commons.Logging;
using Flowa.OrderGenerator.Commons.Observability;
using Flowa.OrderGenerator.Commons.Responses;

namespace Flowa.OrderGenerator.Application.Orders.UseCases;

public sealed class DeleteAllOrdersUseCase
{
    public const string OperationName = "orders.delete-all-orders";
    public const string AllOrdersDeletedMessage = "Todas as ordens foram apagadas.";

    private readonly IStoredOrdersPort _storedOrdersPort;
    private readonly IOperationMonitoring _operationMonitoring;
    private readonly IApplicationLogger<DeleteAllOrdersUseCase> _logger;

    public DeleteAllOrdersUseCase(IStoredOrdersPort storedOrdersPort, IOperationMonitoring operationMonitoring, IApplicationLogger<DeleteAllOrdersUseCase> logger)
    {
        _storedOrdersPort = storedOrdersPort ?? throw new ArgumentNullException(nameof(storedOrdersPort));
        _operationMonitoring = operationMonitoring ?? throw new ArgumentNullException(nameof(operationMonitoring));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<DataMessage<bool>> DeleteAllOrdersAsync(CancellationToken cancellationToken)
    {
        using var ordersDeletion = _operationMonitoring.StartOperationMonitoring(OperationName);
        try
        {
            await _storedOrdersPort.DeleteAllStoredOrdersAsync(cancellationToken);

            ordersDeletion.RecordOperationResult(OperationResults.Succeeded);
            _logger.LogInformation("All stored orders deleted.");
            return DataMessage<bool>.CreateSuccessMessage(true, AllOrdersDeletedMessage);
        }
        catch (Exception ordersDeletionFailure)
        {
            if (UseCaseFailureDataMessageMapper.WasCancelledByTheCaller(ordersDeletionFailure, cancellationToken))
            {
                ordersDeletion.RecordOperationResult(OperationResults.CancelledByTheCaller);
                throw;
            }

            ordersDeletion.RecordOperationResult(OperationResults.Failed);
            return UseCaseFailureDataMessageMapper.MapFailureToDataMessage<bool>(ordersDeletionFailure);
        }
    }
}
