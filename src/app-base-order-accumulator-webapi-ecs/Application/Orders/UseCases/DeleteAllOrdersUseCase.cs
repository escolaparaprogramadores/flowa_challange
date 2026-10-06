using Base.OrderAccumulator.Application.ErrorHandling;
using Base.OrderAccumulator.Application.Exposures.Interfaces;
using Base.OrderAccumulator.Application.Orders.Responses;
using Base.OrderAccumulator.Commons.Database;
using Base.OrderAccumulator.Commons.Responses;
using Base.OrderAccumulator.Domain.Exposures.Interfaces;
using Base.OrderAccumulator.Domain.Orders.Interfaces;
using Base.OrderAccumulator.Domain.Orders.ValueObjects;

namespace Base.OrderAccumulator.Application.Orders.UseCases;

public sealed class DeleteAllOrdersUseCase
{
    public const string AllOrdersDeletedMessage = "Ordens apagadas e exposição zerada.";

    private readonly IUnitOfWork unitOfWork;
    private readonly IOrderRepository orderRepository;
    private readonly IExposureRepository exposureRepository;
    private readonly ISymbolExposureMemoryPort symbolExposureMemory;

    public DeleteAllOrdersUseCase(
        IUnitOfWork unitOfWork,
        IOrderRepository orderRepository,
        IExposureRepository exposureRepository,
        ISymbolExposureMemoryPort symbolExposureMemory)
    {
        this.unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        this.orderRepository = orderRepository ?? throw new ArgumentNullException(nameof(orderRepository));
        this.exposureRepository = exposureRepository ?? throw new ArgumentNullException(nameof(exposureRepository));
        this.symbolExposureMemory = symbolExposureMemory ?? throw new ArgumentNullException(nameof(symbolExposureMemory));
    }

    public async Task<DataMessage<AllOrdersDeletedResponse>> DeleteAllOrdersAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await symbolExposureMemory.DeleteAllOrdersAndZeroExposuresAsync(DeleteAllStoredOrdersAndZeroExposuresAsync, cancellationToken);
            return DataMessage<AllOrdersDeletedResponse>.CreateSuccessMessage(
                new AllOrdersDeletedResponse(OrderFieldPolicy.AllowedOrderSymbols), AllOrdersDeletedMessage);
        }
        catch (Exception deleteAllOrdersFailure)
        {
            await unitOfWork.RollbackTransactionAsync(CancellationToken.None);
            return UseCaseFailureDataMessageMapper.MapUseCaseFailureToDataMessage<AllOrdersDeletedResponse>(deleteAllOrdersFailure);
        }
    }

    private async Task DeleteAllStoredOrdersAndZeroExposuresAsync()
    {
        await unitOfWork.BeginTransactionAsync(CancellationToken.None);
        await exposureRepository.ZeroSymbolExposuresAsync(OrderFieldPolicy.AllowedOrderSymbols, CancellationToken.None);
        await orderRepository.DeleteAllOrdersAsync(CancellationToken.None);
        await unitOfWork.CommitTransactionAsync(CancellationToken.None);
    }
}
