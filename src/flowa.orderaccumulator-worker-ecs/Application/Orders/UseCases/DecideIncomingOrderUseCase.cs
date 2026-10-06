using Flowa.OrderAccumulator.Application.ErrorHandling;
using Flowa.OrderAccumulator.Application.Exposures.Interfaces;
using Flowa.OrderAccumulator.Application.Orders.Interfaces;
using Flowa.OrderAccumulator.Application.Orders.Responses;
using Flowa.Commons.Database;
using Flowa.Commons.Logging;
using Flowa.Commons.Observability;
using Flowa.Commons.Responses;
using Flowa.OrderAccumulator.Domain.DomainServices;
using Flowa.OrderAccumulator.Domain.Orders.Interfaces;
using Flowa.OrderAccumulator.Domain.Orders.ValueObjects;

namespace Flowa.OrderAccumulator.Application.Orders.UseCases;

public sealed class DecideIncomingOrderUseCase
{
    public const string OrderAnsweredMessage = "Ordem respondida.";
    public const string OperationName = "orders.decide-incoming-order";
    public const string AcceptedOrderResult = "accepted";
    public const string RejectedOrderResult = "rejected";
    public const string RepeatedOrderResult = "repeated";

    private readonly IUnitOfWork unitOfWork;
    private readonly IOrderRepository orderRepository;
    private readonly OrderDecisionDomainService orderDecisionDomainService;
    private readonly ISymbolExposureMemoryPort symbolExposureMemory;
    private readonly IOrderMetricsPort orderMetrics;
    private readonly IOperationMonitoring operationMonitoring;
    private readonly IApplicationLogger<DecideIncomingOrderUseCase> orderDecisionLogger;

    public DecideIncomingOrderUseCase(
        IUnitOfWork unitOfWork,
        IOrderRepository orderRepository,
        OrderDecisionDomainService orderDecisionDomainService,
        ISymbolExposureMemoryPort symbolExposureMemory,
        IOrderMetricsPort orderMetrics,
        IOperationMonitoring operationMonitoring,
        IApplicationLogger<DecideIncomingOrderUseCase> orderDecisionLogger)
    {
        this.unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        this.orderRepository = orderRepository ?? throw new ArgumentNullException(nameof(orderRepository));
        this.orderDecisionDomainService = orderDecisionDomainService ?? throw new ArgumentNullException(nameof(orderDecisionDomainService));
        this.symbolExposureMemory = symbolExposureMemory ?? throw new ArgumentNullException(nameof(symbolExposureMemory));
        this.orderMetrics = orderMetrics ?? throw new ArgumentNullException(nameof(orderMetrics));
        this.operationMonitoring = operationMonitoring ?? throw new ArgumentNullException(nameof(operationMonitoring));
        this.orderDecisionLogger = orderDecisionLogger ?? throw new ArgumentNullException(nameof(orderDecisionLogger));
    }

    public async Task<DataMessage<DecideIncomingOrderResponse>> DecideIncomingOrderAsync(IncomingOrder incomingOrder, CancellationToken cancellationToken = default)
    {
        using var orderDecisionMonitoring = operationMonitoring.StartOperationMonitoring(OperationName);
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

            var orderDecision = DecideIncomingOrderResponse.MapFromOrderAnswer(orderAnswer);
            orderDecisionMonitoring.RecordOperationResult(ClassifyOrderDecisionResult(orderDecision));
            if (orderDecision is { IsRepeat: false, Accepted: true })
                orderDecisionLogger.LogInformation("Order accepted.", new
                {
                    orderDecision.OrderId,
                    orderDecision.Symbol,
                    orderDecision.Side,
                    orderDecision.Quantity,
                    orderDecision.Price
                });

            return DataMessage<DecideIncomingOrderResponse>.CreateSuccessMessage(orderDecision, OrderAnsweredMessage);
        }
        catch (Exception orderDecisionFailure)
        {
            orderDecisionMonitoring.RecordOperationResult(OperationResults.Failed);
            await unitOfWork.RollbackTransactionAsync(CancellationToken.None);
            return UseCaseFailureDataMessageMapper.MapUseCaseFailureToDataMessage<DecideIncomingOrderResponse>(orderDecisionFailure);
        }
    }

    private static string ClassifyOrderDecisionResult(DecideIncomingOrderResponse orderDecision) => orderDecision switch
    {
        { IsRepeat: true } => RepeatedOrderResult,
        { Accepted: true } => AcceptedOrderResult,
        _ => RejectedOrderResult
    };

    private async Task<OrderAnswer> DecideAndStoreIncomingOrderAsync(IncomingOrder incomingOrder, CancellationToken cancellationToken)
    {
        var storedOrder = await orderRepository.FindOrderByClOrdIdAsync(incomingOrder.ClOrdId, cancellationToken);
        if (storedOrder is not null)
            return OrderAnswer.RepeatStoredAnswer(storedOrder);

        await unitOfWork.BeginTransactionAsync(cancellationToken);
        var answeredOrder = await orderDecisionDomainService.DecideIncomingOrderAsync(incomingOrder, cancellationToken);
        if (await orderRepository.TryAddOrderAsync(answeredOrder, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tokenThatLetsTheSentCommitFinish = CancellationToken.None;
            await unitOfWork.CommitTransactionAsync(tokenThatLetsTheSentCommitFinish);
            return OrderAnswer.AnswerNewOrder(answeredOrder);
        }

        await unitOfWork.RollbackTransactionAsync(cancellationToken);
        var orderStoredInParallel = await orderRepository.FindOrderByClOrdIdAsync(incomingOrder.ClOrdId, cancellationToken)
            ?? throw new InvalidOperationException($"The order {incomingOrder.ClOrdId} collided on the key, but was not found.");
        return OrderAnswer.RepeatStoredAnswer(orderStoredInParallel);
    }
}
