using System.Collections.Concurrent;
using Flowa.OrderAccumulator.Application.Exposures.Interfaces;
using Flowa.OrderAccumulator.Application.Orders.Responses;
using Flowa.OrderAccumulator.Domain.Exposures.ValueObjects;
using Flowa.OrderAccumulator.Domain.Orders.ValueObjects;

namespace Flowa.OrderAccumulator.Infrastructure.Exposures.Adapters;

public sealed class InMemorySymbolExposureAdapter : ISymbolExposureMemoryPort
{
    private readonly ConcurrentDictionary<string, decimal> exposureBySymbol = new();

    private readonly SemaphoreSlim deleteAllOrdersDoor = new(1, 1);
    private readonly SemaphoreSlim noOrderInProgress = new(1, 1);
    private readonly SemaphoreSlim ordersInProgressCountLock = new(1, 1);
    private int ordersInProgressCount;

    public async Task<TOrderDecision> DecideOrderOutsideDeleteAllAsync<TOrderDecision>(Func<Task<TOrderDecision>> decideIncomingOrder, CancellationToken cancellationToken)
    {
        await deleteAllOrdersDoor.WaitAsync(cancellationToken);
        deleteAllOrdersDoor.Release();

        await ordersInProgressCountLock.WaitAsync(CancellationToken.None);
        if (++ordersInProgressCount == 1)
            await noOrderInProgress.WaitAsync(CancellationToken.None);
        ordersInProgressCountLock.Release();

        try
        {
            return await decideIncomingOrder();
        }
        finally
        {
            await ordersInProgressCountLock.WaitAsync(CancellationToken.None);
            if (--ordersInProgressCount == 0)
                noOrderInProgress.Release();
            ordersInProgressCountLock.Release();
        }
    }

    public async Task DeleteAllOrdersAndZeroExposuresAsync(Func<Task> deleteAllStoredOrdersAndZeroExposures, CancellationToken cancellationToken)
    {
        await deleteAllOrdersDoor.WaitAsync(cancellationToken);
        try
        {
            await noOrderInProgress.WaitAsync(CancellationToken.None);
            try
            {
                await deleteAllStoredOrdersAndZeroExposures();
                foreach (var orderSymbol in OrderFieldPolicy.AllowedOrderSymbols)
                    exposureBySymbol[orderSymbol] = 0m;
            }
            finally
            {
                noOrderInProgress.Release();
            }
        }
        finally
        {
            deleteAllOrdersDoor.Release();
        }
    }

    public void LoadStoredExposures(IEnumerable<SymbolExposure> storedSymbolExposures)
    {
        foreach (var storedSymbolExposure in storedSymbolExposures)
            exposureBySymbol[storedSymbolExposure.Symbol] = storedSymbolExposure.Exposure;
    }

    public void ApplyAcceptedOrder(DecideIncomingOrderResponse acceptedOrder)
    {
        ArgumentNullException.ThrowIfNull(acceptedOrder);
        var acceptedOrderExposureDelta = ExposureLimitPolicy.CalculateAcceptedOrderExposureDelta(acceptedOrder.Side, acceptedOrder.Quantity, acceptedOrder.Price);
        exposureBySymbol.AddOrUpdate(
            acceptedOrder.Symbol!, acceptedOrderExposureDelta, (_, currentSymbolExposure) => currentSymbolExposure + acceptedOrderExposureDelta);
    }

    public IReadOnlyList<SymbolExposure> ReadCurrentSymbolExposures() =>
        OrderFieldPolicy.AllowedOrderSymbols
            .Select(orderSymbol => new SymbolExposure(orderSymbol, exposureBySymbol.GetValueOrDefault(orderSymbol)))
            .ToList();
}
