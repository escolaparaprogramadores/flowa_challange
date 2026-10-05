using Base.OrderAccumulator.Application.Exposures;
using Base.OrderAccumulator.Commons;
using Base.OrderAccumulator.Domain.Orders;

namespace Base.OrderAccumulator.Application.Orders.DecideIncomingOrder;

// The flow of one order that arrived by FIX: a repeated ClOrdID gets the stored answer back; a new
// one is decided by the domain and stored in the same transaction that moved the exposure.
public sealed class DecideIncomingOrderUseCase(
    IUnitOfWork unitOfWork,
    IOrderRepository orderRepository,
    OrderDecisionDomainService orderDecisionDomainService,
    SymbolExposureMemoryService symbolExposureMemory,
    IOrderMetricsPort orderMetrics)
{
    public async Task<DecideIncomingOrderOutput> DecideIncomingOrderAsync(IncomingOrder incomingOrder, CancellationToken cancellationToken = default)
    {
        // Without a ClOrdID a repeat cannot be recognised: different orders would collide on the same key.
        ArgumentException.ThrowIfNullOrWhiteSpace(incomingOrder.ClOrdId);

        // Storing in the database and adding to memory happen together, with no "Delete all" in between.
        var orderDecision = await symbolExposureMemory.DecideOrderOutsideDeleteAllAsync(async () =>
        {
            var storedOrderDecision = await DecideAndStoreIncomingOrderAsync(incomingOrder, cancellationToken);
            if (storedOrderDecision is { IsRepeat: false, Accepted: true })
                symbolExposureMemory.ApplyAcceptedOrder(storedOrderDecision);
            return storedOrderDecision;
        }, cancellationToken);

        // A repeat returns the old answer and is not counted again; an exception passes through uncounted.
        if (!orderDecision.IsRepeat)
            orderMetrics.CountAnsweredOrder(orderDecision.Symbol, orderDecision.Side, orderDecision.Accepted);

        return orderDecision;
    }

    private async Task<DecideIncomingOrderOutput> DecideAndStoreIncomingOrderAsync(IncomingOrder incomingOrder, CancellationToken cancellationToken)
    {
        // A stored repeat returns at once, without competing with new orders for the symbol row lock.
        var storedOrder = await orderRepository.FindOrderByClOrdIdAsync(incomingOrder.ClOrdId, cancellationToken);
        if (storedOrder is not null)
            return DecideIncomingOrderOutput.FromAnsweredOrder(storedOrder, isRepeat: true);

        await unitOfWork.BeginTransactionAsync(cancellationToken);
        try
        {
            var answeredOrder = await orderDecisionDomainService.DecideIncomingOrderAsync(incomingOrder, cancellationToken);
            if (await orderRepository.TryAddOrderAsync(answeredOrder, cancellationToken))
            {
                await unitOfWork.CommitTransactionAsync(cancellationToken);
                return DecideIncomingOrderOutput.FromAnsweredOrder(answeredOrder, isRepeat: false);
            }

            // The same order arrived in parallel and the other one stored first: the rollback undoes what this
            // attempt changed in the exposure.
            await unitOfWork.RollbackTransactionAsync(cancellationToken);
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync(CancellationToken.None);
            throw;
        }

        var orderStoredInParallel = await orderRepository.FindOrderByClOrdIdAsync(incomingOrder.ClOrdId, cancellationToken)
            ?? throw new InvalidOperationException($"The order {incomingOrder.ClOrdId} collided on the key, but was not found.");
        return DecideIncomingOrderOutput.FromAnsweredOrder(orderStoredInParallel, isRepeat: true);
    }
}
