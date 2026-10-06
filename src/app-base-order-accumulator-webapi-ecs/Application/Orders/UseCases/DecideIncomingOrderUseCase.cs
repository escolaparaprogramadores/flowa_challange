using Base.OrderAccumulator.Application.Exposures.Interfaces;
using Base.OrderAccumulator.Application.Orders.Interfaces;
using Base.OrderAccumulator.Application.Orders.Responses;
using Base.OrderAccumulator.Commons.Database;
using Base.OrderAccumulator.Domain.DomainServices;
using Base.OrderAccumulator.Domain.Orders.Interfaces;
using Base.OrderAccumulator.Domain.Orders.ValueObjects;

namespace Base.OrderAccumulator.Application.Orders.UseCases;

public sealed class DecideIncomingOrderUseCase(
    IUnitOfWork unitOfWork,
    IOrderRepository orderRepository,
    OrderDecisionDomainService orderDecisionDomainService,
    ISymbolExposureMemoryPort symbolExposureMemory,
    IOrderMetricsPort orderMetrics)
{
    public async Task<DecideIncomingOrderResponse> DecideIncomingOrderAsync(IncomingOrder incomingOrder, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(incomingOrder.ClOrdId);

        var orderDecision = await symbolExposureMemory.DecideOrderOutsideDeleteAllAsync(async () =>
        {
            var storedOrderDecision = await DecideAndStoreIncomingOrderAsync(incomingOrder, cancellationToken);
            if (storedOrderDecision is { IsRepeat: false, Accepted: true })
                symbolExposureMemory.ApplyAcceptedOrder(storedOrderDecision);
            return storedOrderDecision;
        }, cancellationToken);

        if (!orderDecision.IsRepeat)
            orderMetrics.CountAnsweredOrder(orderDecision.Symbol, orderDecision.Side, orderDecision.Accepted);

        return orderDecision;
    }

    private async Task<DecideIncomingOrderResponse> DecideAndStoreIncomingOrderAsync(IncomingOrder incomingOrder, CancellationToken cancellationToken)
    {
        var storedOrder = await orderRepository.FindOrderByClOrdIdAsync(incomingOrder.ClOrdId, cancellationToken);
        if (storedOrder is not null)
            return DecideIncomingOrderResponse.FromAnsweredOrder(storedOrder, isRepeat: true);

        await unitOfWork.BeginTransactionAsync(cancellationToken);
        try
        {
            var answeredOrder = await orderDecisionDomainService.DecideIncomingOrderAsync(incomingOrder, cancellationToken);
            if (await orderRepository.TryAddOrderAsync(answeredOrder, cancellationToken))
            {
                await unitOfWork.CommitTransactionAsync(cancellationToken);
                return DecideIncomingOrderResponse.FromAnsweredOrder(answeredOrder, isRepeat: false);
            }

            await unitOfWork.RollbackTransactionAsync(cancellationToken);
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync(CancellationToken.None);
            throw;
        }

        var orderStoredInParallel = await orderRepository.FindOrderByClOrdIdAsync(incomingOrder.ClOrdId, cancellationToken)
            ?? throw new InvalidOperationException($"The order {incomingOrder.ClOrdId} collided on the key, but was not found.");
        return DecideIncomingOrderResponse.FromAnsweredOrder(orderStoredInParallel, isRepeat: true);
    }
}
