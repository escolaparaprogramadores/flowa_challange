using System.Data;
using Flowa.OrderGenerator.Application.ErrorHandling;
using Flowa.OrderGenerator.Application.Exposures.Interfaces;
using Flowa.OrderGenerator.Application.Orders.Interfaces;
using Flowa.Commons.Database;
using Flowa.Commons.Logging;
using Flowa.Commons.Observability;
using Flowa.Commons.Responses;

namespace Flowa.OrderGenerator.Application.Orders.UseCases;

public sealed class DeleteAllOrdersUseCase
{
    public const string OperationName = "orders.delete-all-orders";
    public const string AllOrdersDeletedMessage = "Todas as ordens foram apagadas.";

    private readonly ISymbolExposureRepository _symbolExposureRepository;
    private readonly IStoredOrderRepository _storedOrderRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IOperationMonitoring _operationMonitoring;
    private readonly IApplicationLogger<DeleteAllOrdersUseCase> _logger;

    public DeleteAllOrdersUseCase(
        ISymbolExposureRepository symbolExposureRepository, IStoredOrderRepository storedOrderRepository, IUnitOfWork unitOfWork,
        IOperationMonitoring operationMonitoring, IApplicationLogger<DeleteAllOrdersUseCase> logger)
    {
        _symbolExposureRepository = symbolExposureRepository ?? throw new ArgumentNullException(nameof(symbolExposureRepository));
        _storedOrderRepository = storedOrderRepository ?? throw new ArgumentNullException(nameof(storedOrderRepository));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _operationMonitoring = operationMonitoring ?? throw new ArgumentNullException(nameof(operationMonitoring));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<DataMessage<bool>> DeleteAllOrdersAsync(CancellationToken cancellationToken)
    {
        using var ordersDeletion = _operationMonitoring.StartOperationMonitoring(OperationName);
        try
        {
            await _unitOfWork.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            if (await _symbolExposureRepository.ExposureTableExistsAsync(cancellationToken))
                await _symbolExposureRepository.ZeroSymbolExposuresAsync(cancellationToken);
            if (await _storedOrderRepository.OrderTableExistsAsync(cancellationToken))
                await _storedOrderRepository.DeleteAllStoredOrdersAsync(cancellationToken);
            await _unitOfWork.CommitTransactionAsync(cancellationToken);

            ordersDeletion.RecordOperationResult(OperationResults.Succeeded);
            _logger.LogInformation("All stored orders deleted.");
            return DataMessage<bool>.CreateSuccessMessage(true, AllOrdersDeletedMessage);
        }
        catch (Exception ordersDeletionFailure)
        {
            await _unitOfWork.RollbackTransactionAsync(CancellationToken.None);
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
