using Base.OrderAccumulator.Application.Exposures;
using Base.OrderAccumulator.Commons;
using Base.OrderAccumulator.Domain.Exposures;
using Base.OrderAccumulator.Domain.Orders;
using Flowa.Shared;

namespace Base.OrderAccumulator.Application.Orders.DeleteAllOrders;

// "Delete all": database and memory are zeroed together, with no order in between.
public sealed class DeleteAllOrdersUseCase(
    IUnitOfWork unitOfWork,
    IOrderRepository orderRepository,
    IExposureRepository exposureRepository,
    SymbolExposureMemoryService symbolExposureMemory)
{
    public Task DeleteAllOrdersAsync(CancellationToken cancellationToken = default) =>
        symbolExposureMemory.DeleteAllOrdersAndZeroExposuresAsync(DeleteAllStoredOrdersAndZeroExposuresAsync, cancellationToken);

    // Once inside, the delete runs to the end even if the client gives up. All or nothing: first it
    // zeroes the exposure of the three symbols, then deletes the orders.
    private async Task DeleteAllStoredOrdersAndZeroExposuresAsync()
    {
        await unitOfWork.BeginTransactionAsync(CancellationToken.None);
        try
        {
            await exposureRepository.ZeroSymbolExposuresAsync(OrderRules.AllowedOrderSymbols, CancellationToken.None);
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
