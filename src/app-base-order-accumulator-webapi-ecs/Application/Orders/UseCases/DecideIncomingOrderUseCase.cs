using Base.OrderAccumulator.Application.ErrorHandling;
using Base.OrderAccumulator.Application.Exposures.Interfaces;
using Base.OrderAccumulator.Application.Orders.Interfaces;
using Base.OrderAccumulator.Application.Orders.Responses;
using Base.OrderAccumulator.Commons.Database;
using Base.OrderAccumulator.Commons.Responses;
using Base.OrderAccumulator.Domain.DomainServices;
using Base.OrderAccumulator.Domain.Orders.Interfaces;
using Base.OrderAccumulator.Domain.Orders.ValueObjects;

namespace Base.OrderAccumulator.Application.Orders.UseCases;

public sealed class DecideIncomingOrderUseCase
{
    public const string OrderAnsweredMessage = "Ordem respondida.";

    private readonly IUnitOfWork unitOfWork;
    private readonly IOrderRepository orderRepository;
    private readonly OrderDecisionDomainService orderDecisionDomainService;
    private readonly ISymbolExposureMemoryPort symbolExposureMemory;
    private readonly IOrderMetricsPort orderMetrics;

    public DecideIncomingOrderUseCase(
        IUnitOfWork unitOfWork,
        IOrderRepository orderRepository,
        OrderDecisionDomainService orderDecisionDomainService,
        ISymbolExposureMemoryPort symbolExposureMemory,
        IOrderMetricsPort orderMetrics)
    {
        this.unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        this.orderRepository = orderRepository ?? throw new ArgumentNullException(nameof(orderRepository));
        this.orderDecisionDomainService = orderDecisionDomainService ?? throw new ArgumentNullException(nameof(orderDecisionDomainService));
        this.symbolExposureMemory = symbolExposureMemory ?? throw new ArgumentNullException(nameof(symbolExposureMemory));
        this.orderMetrics = orderMetrics ?? throw new ArgumentNullException(nameof(orderMetrics));
    }

    public async Task<DataMessage<DecideIncomingOrderResponse>> DecideIncomingOrderAsync(IncomingOrder incomingOrder, CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(incomingOrder);

            var orderAnswer = await symbolExposureMemory.DecideOrderOutsideDeleteAllAsync(async () =>
            {
                var storedOrderAnswer = await DecideAndStoreIncomingOrderAsync(incomingOrder, cancellationToken);
                if (storedOrderAnswer.ShouldMoveSymbolExposure())
                    symbolExposureMemory.ApplyAcceptedOrder(DecideIncomingOrderResponse.MapFromOrderAnswer(storedOrderAnswer));
                return storedOrderAnswer;
            }, cancellationToken);

            var answeredOrder = orderAnswer.AnsweredOrder;
            if (orderAnswer.ShouldCountInOrderMetrics())
                orderMetrics.CountAnsweredOrder(answeredOrder.Symbol, answeredOrder.Side, answeredOrder.Accepted);

            return DataMessage<DecideIncomingOrderResponse>.CreateSuccessMessage(DecideIncomingOrderResponse.MapFromOrderAnswer(orderAnswer), OrderAnsweredMessage);
        }
        catch (Exception orderDecisionFailure)
        {
            await unitOfWork.RollbackTransactionAsync(CancellationToken.None);
            return UseCaseFailureDataMessageMapper.MapUseCaseFailureToDataMessage<DecideIncomingOrderResponse>(orderDecisionFailure);
        }
    }

    private async Task<OrderAnswer> DecideAndStoreIncomingOrderAsync(IncomingOrder incomingOrder, CancellationToken cancellationToken)
    {
        var storedOrder = await orderRepository.FindOrderByClOrdIdAsync(incomingOrder.ClOrdId, cancellationToken);
        if (storedOrder is not null)
            return OrderAnswer.RepeatStoredAnswer(storedOrder);

        await unitOfWork.BeginTransactionAsync(cancellationToken);
        var answeredOrder = await orderDecisionDomainService.DecideIncomingOrderAsync(incomingOrder, cancellationToken);
        if (await orderRepository.TryAddOrderAsync(answeredOrder, cancellationToken))
        {
            await unitOfWork.CommitTransactionAsync(cancellationToken);
            return OrderAnswer.AnswerNewOrder(answeredOrder);
        }

        await unitOfWork.RollbackTransactionAsync(cancellationToken);
        var orderStoredInParallel = await orderRepository.FindOrderByClOrdIdAsync(incomingOrder.ClOrdId, cancellationToken)
            ?? throw new InvalidOperationException($"The order {incomingOrder.ClOrdId} collided on the key, but was not found.");
        return OrderAnswer.RepeatStoredAnswer(orderStoredInParallel);
    }
}
