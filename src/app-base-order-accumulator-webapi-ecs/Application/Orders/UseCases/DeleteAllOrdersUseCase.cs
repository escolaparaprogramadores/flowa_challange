using Base.OrderAccumulator.Application.Exposures.Interfaces;
using Base.OrderAccumulator.Commons.Database;
using Base.OrderAccumulator.Domain.Exposures.Interfaces;
using Base.OrderAccumulator.Domain.Orders.Interfaces;
using Base.OrderAccumulator.Domain.Orders.ValueObjects;

namespace Base.OrderAccumulator.Application.Orders.UseCases;

public sealed class DeleteAllOrdersUseCase(
    IUnitOfWork unitOfWork,
    IOrderRepository orderRepository,
    IExposureRepository exposureRepository,
    ISymbolExposureMemoryPort symbolExposureMemory)
{
    public Task DeleteAllOrdersAsync(CancellationToken cancellationToken = default) =>
        symbolExposureMemory.DeleteAllOrdersAndZeroExposuresAsync(DeleteAllStoredOrdersAndZeroExposuresAsync, cancellationToken);

    private async Task DeleteAllStoredOrdersAndZeroExposuresAsync()
    {
        await unitOfWork.BeginTransactionAsync(CancellationToken.None);
        try
        {
            await exposureRepository.ZeroSymbolExposuresAsync(OrderFieldPolicy.AllowedOrderSymbols, CancellationToken.None);
            await orderRepository.DeleteAllOrdersAsync(CancellationToken.None);
            await unitOfWork.CommitTransactionAsync(CancellationToken.None);
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync(CancellationToken.None);
            throw;
        }
    }
}
