using System.Collections.Concurrent;
using Base.OrderAccumulator.Application.Orders.DecideIncomingOrder;
using Base.OrderAccumulator.Domain.Exposures;
using Base.OrderAccumulator.Domain.Orders;

namespace Base.OrderAccumulator.Application.Exposures;

// The exposure of each symbol, kept in the process so the gauge does not query the database on every send.
// This holds because the OrderAccumulator runs as a single task (infra/servicos.tf, desired_count = 1).
public sealed class SymbolExposureMemoryService
{
    private readonly ConcurrentDictionary<string, decimal> exposureBySymbol = new();

    // An order and "Delete all" never interleave: otherwise an order stored before the delete would add to memory
    // after the zero, and the gauge would show a value the database does not have. Orders go in together; the delete
    // closes the door to new orders, waits for the ones inside to leave and goes in alone.
    private readonly SemaphoreSlim deleteAllOrdersDoor = new(1, 1);
    private readonly SemaphoreSlim noOrderInProgress = new(1, 1);
    private readonly SemaphoreSlim ordersInProgressCountLock = new(1, 1);
    private int ordersInProgressCount;

    public async Task<DecideIncomingOrderOutput> DecideOrderOutsideDeleteAllAsync(Func<Task<DecideIncomingOrderOutput>> decideIncomingOrder, CancellationToken cancellationToken)
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

    // Memory is zeroed only if the database confirmed the delete; a database failure leaves both as they were.
    public async Task DeleteAllOrdersAndZeroExposuresAsync(Func<Task> deleteAllStoredOrdersAndZeroExposures, CancellationToken cancellationToken)
    {
        await deleteAllOrdersDoor.WaitAsync(cancellationToken);
        try
        {
            await noOrderInProgress.WaitAsync(CancellationToken.None);
            try
            {
                await deleteAllStoredOrdersAndZeroExposures();
                foreach (var orderSymbol in OrderFieldRule.AllowedOrderSymbols)
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

    // Only an accepted order gets here, so symbol, side and quantity already passed validation.
    public void ApplyAcceptedOrder(DecideIncomingOrderOutput acceptedOrder)
    {
        var acceptedOrderSide = acceptedOrder.Side == OrderSideCodes.BuyOrderSideFixCode ? OrderSide.Buy : OrderSide.Sell;
        var acceptedOrderExposureDelta = ExposureLimitPolicy.CalculateOrderExposureDelta(acceptedOrderSide, (int)acceptedOrder.Quantity, acceptedOrder.Price);
        exposureBySymbol.AddOrUpdate(
            acceptedOrder.Symbol!, acceptedOrderExposureDelta, (_, currentSymbolExposure) => currentSymbolExposure + acceptedOrderExposureDelta);
    }

    public IReadOnlyList<SymbolExposure> ReadCurrentSymbolExposures() =>
        OrderFieldRule.AllowedOrderSymbols
            .Select(orderSymbol => new SymbolExposure(orderSymbol, exposureBySymbol.GetValueOrDefault(orderSymbol)))
            .ToList();
}
