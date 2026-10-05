using System.Collections.Concurrent;
using Base.OrderAccumulator.Application.Orders.DecideIncomingOrder;
using Base.OrderAccumulator.Domain.Exposures;
using Flowa.Shared;

namespace Base.OrderAccumulator.Application.Exposures;

// A exposição de cada símbolo, mantida no processo para o gauge não consultar o banco a cada envio.
// Vale porque o OrderAccumulator roda numa task só (infra/servicos.tf, desired_count = 1).
public sealed class SymbolExposureMemoryService
{
    private readonly ConcurrentDictionary<string, decimal> exposureBySymbol = new();

    // Ordem e "Deletar tudo" não se intercalam: senão uma ordem gravada antes do apagar somaria na memória
    // depois do zero, e o gauge mostraria um valor que o banco não tem. Ordens entram juntas; o apagar fecha
    // a porta para ordens novas, espera as que estão dentro saírem e entra sozinho.
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

    // A memória só zera se o banco confirmou o apagar; falha no banco deixa as duas como estavam.
    public async Task DeleteAllOrdersAndZeroExposuresAsync(Func<Task> deleteAllStoredOrdersAndZeroExposures, CancellationToken cancellationToken)
    {
        await deleteAllOrdersDoor.WaitAsync(cancellationToken);
        try
        {
            await noOrderInProgress.WaitAsync(CancellationToken.None);
            try
            {
                await deleteAllStoredOrdersAndZeroExposures();
                foreach (var orderSymbol in OrderRules.AllowedOrderSymbols)
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

    // Só ordem aceita chega aqui, então símbolo, lado e quantidade já passaram pela validação.
    public void ApplyAcceptedOrder(DecideIncomingOrderOutput acceptedOrder)
    {
        var acceptedOrderSide = acceptedOrder.Side == OrderSideCodes.BuyOrderSideFixCode ? OrderSide.Buy : OrderSide.Sell;
        var acceptedOrderExposureDelta = ExposureLimitPolicy.CalculateOrderExposureDelta(acceptedOrderSide, (int)acceptedOrder.Quantity, acceptedOrder.Price);
        exposureBySymbol.AddOrUpdate(
            acceptedOrder.Symbol!, acceptedOrderExposureDelta, (_, currentSymbolExposure) => currentSymbolExposure + acceptedOrderExposureDelta);
    }

    public IReadOnlyList<SymbolExposure> ReadCurrentSymbolExposures() =>
        OrderRules.AllowedOrderSymbols
            .Select(orderSymbol => new SymbolExposure(orderSymbol, exposureBySymbol.GetValueOrDefault(orderSymbol)))
            .ToList();
}
