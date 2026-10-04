using System.Collections.Concurrent;
using Flowa.Shared;
using OrderAccumulator.Exposure;

namespace OrderAccumulator.Observabilidade;

// Nomes combinados com o painel do Datadog (observabilidade/datadog/). Mudar aqui apaga o gráfico.
public static class OrderMetricNames
{
    public const string AcceptedOrders = "flowa.ordens.aceitas";
    public const string RejectedOrders = "flowa.ordens.rejeitadas";
    public const string SymbolExposure = "flowa.exposicao";
}

// Etiquetas com valores fechados: cada valor novo vira uma série paga no Datadog.
// Símbolo fora da lista conta numa série só, para o total não passar de 20 séries.
public static class OrderMetricTags
{
    public const string InvalidTagValue = "invalido";

    public static string[] OrderOutcomeTags(string? orderSymbol, char orderSide)
    {
        if (orderSymbol is null || !OrderRules.AllowedOrderSymbols.Contains(orderSymbol))
            return [$"symbol:{InvalidTagValue}", $"side:{InvalidTagValue}"];

        return [$"symbol:{orderSymbol}", $"side:{OrderSideTagValue(orderSide)}"];
    }

    public static string[] SymbolExposureTags(string orderSymbol) => [$"symbol:{orderSymbol}"];

    private static string OrderSideTagValue(char orderSide) => orderSide switch
    {
        OrderSideCodes.BuyOrderSideFixCode => OrderSideCodes.BuyOrderSideJsonCode,
        OrderSideCodes.SellOrderSideFixCode => OrderSideCodes.SellOrderSideJsonCode,
        _ => InvalidTagValue
    };
}

// A exposição de cada símbolo, mantida no processo para o gauge não consultar o banco a cada envio.
// Vale porque o OrderAccumulator roda numa task só (infra/servicos.tf, desired_count = 1).
public sealed class SymbolExposureMemory
{
    private readonly ConcurrentDictionary<string, decimal> exposureBySymbol = new();

    // Ordem e "Deletar tudo" não se intercalam: senão uma ordem gravada antes do apagar somaria na memória
    // depois do zero, e o gauge mostraria um valor que o banco não tem. Ordens entram juntas; o apagar fecha
    // a porta para ordens novas, espera as que estão dentro saírem e entra sozinho.
    private readonly SemaphoreSlim deleteAllOrdersDoor = new(1, 1);
    private readonly SemaphoreSlim noOrderInProgress = new(1, 1);
    private readonly SemaphoreSlim ordersInProgressCountLock = new(1, 1);
    private int ordersInProgressCount;

    public async Task<OrderOutcome> ProcessOrderOutsideDeleteAllAsync(Func<Task<OrderOutcome>> processIncomingOrder, CancellationToken cancellationToken)
    {
        await deleteAllOrdersDoor.WaitAsync(cancellationToken);
        deleteAllOrdersDoor.Release();

        await ordersInProgressCountLock.WaitAsync(CancellationToken.None);
        if (++ordersInProgressCount == 1)
            await noOrderInProgress.WaitAsync(CancellationToken.None);
        ordersInProgressCountLock.Release();

        try
        {
            return await processIncomingOrder();
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
    public void ApplyAcceptedOrder(OrderOutcome acceptedOrder)
    {
        var acceptedOrderSide = acceptedOrder.Side == OrderSideCodes.BuyOrderSideFixCode ? OrderSide.Buy : OrderSide.Sell;
        var acceptedOrderExposureDelta = ExposureLimit.OrderExposureDelta(acceptedOrderSide, (int)acceptedOrder.Quantity, acceptedOrder.Price);
        exposureBySymbol.AddOrUpdate(
            acceptedOrder.Symbol!, acceptedOrderExposureDelta, (_, currentSymbolExposure) => currentSymbolExposure + acceptedOrderExposureDelta);
    }

    public IReadOnlyList<SymbolExposure> CurrentSymbolExposures() =>
        OrderRules.AllowedOrderSymbols
            .Select(orderSymbol => new SymbolExposure(orderSymbol, exposureBySymbol.GetValueOrDefault(orderSymbol)))
            .ToList();
}
