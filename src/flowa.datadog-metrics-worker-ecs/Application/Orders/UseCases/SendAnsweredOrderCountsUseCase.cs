using Flowa.Commons.Logging;
using Flowa.Commons.Observability;
using Flowa.Commons.Responses;
using Flowa.DatadogMetrics.Application.ErrorHandling;
using Flowa.DatadogMetrics.Application.Orders.Commands;
using Flowa.DatadogMetrics.Application.Orders.Interfaces;
using Flowa.DatadogMetrics.Application.Orders.Responses;

namespace Flowa.DatadogMetrics.Application.Orders.UseCases;

public sealed class SendAnsweredOrderCountsUseCase
{
    public const string CountStartMessage = "Contagem de ordens começa depois das ordens já gravadas.";
    public const string OrderCountsSentMessage = "Contagem de ordens enviada ao Datadog.";
    public const string OperationName = "orders.send-answered-order-counts";

    private readonly IAnsweredOrderCountReadRepository answeredOrderCountReadRepository;
    private readonly IOrderMetricsPort orderMetrics;
    private readonly IOperationMonitoring operationMonitoring;
    private readonly IApplicationLogger<SendAnsweredOrderCountsUseCase> orderCountsLogger;

    public SendAnsweredOrderCountsUseCase(
        IAnsweredOrderCountReadRepository answeredOrderCountReadRepository,
        IOrderMetricsPort orderMetrics,
        IOperationMonitoring operationMonitoring,
        IApplicationLogger<SendAnsweredOrderCountsUseCase> orderCountsLogger)
    {
        this.answeredOrderCountReadRepository = answeredOrderCountReadRepository ?? throw new ArgumentNullException(nameof(answeredOrderCountReadRepository));
        this.orderMetrics = orderMetrics ?? throw new ArgumentNullException(nameof(orderMetrics));
        this.operationMonitoring = operationMonitoring ?? throw new ArgumentNullException(nameof(operationMonitoring));
        this.orderCountsLogger = orderCountsLogger ?? throw new ArgumentNullException(nameof(orderCountsLogger));
    }

    public async Task<DataMessage<SendAnsweredOrderCountsResponse>> SendAnsweredOrderCountsAsync(
        SendAnsweredOrderCountsCommand sendOrderCountsCommand, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sendOrderCountsCommand);
        using var orderCountsMonitoring = operationMonitoring.StartOperationMonitoring(OperationName);
        try
        {
            if (sendOrderCountsCommand.LastCountedOrderId is not { } lastCountedOrderId)
            {
                var lastStoredOrderId = await answeredOrderCountReadRepository.GetLastStoredOrderIdAsync(cancellationToken);
                orderCountsLogger.LogInformation("Order count starts after the stored orders.", new { LastCountedOrderId = lastStoredOrderId });
                return DataMessage<SendAnsweredOrderCountsResponse>.CreateSuccessMessage(
                    new SendAnsweredOrderCountsResponse(lastStoredOrderId, 0), CountStartMessage);
            }

            var answeredOrderCounts = await answeredOrderCountReadRepository.GetAnsweredOrderCountsAfterAsync(lastCountedOrderId, cancellationToken);
            orderMetrics.SendAnsweredOrderCounts(answeredOrderCounts);

            var sentOrderCounts = new SendAnsweredOrderCountsResponse(
                answeredOrderCounts.Select(answeredOrderCount => answeredOrderCount.LastOrderId).DefaultIfEmpty(lastCountedOrderId).Max(),
                answeredOrderCounts.Sum(answeredOrderCount => answeredOrderCount.OrderCount));
            orderCountsLogger.LogInformation("Answered order counts sent.", new { sentOrderCounts.CountedOrders, sentOrderCounts.LastCountedOrderId });
            return DataMessage<SendAnsweredOrderCountsResponse>.CreateSuccessMessage(sentOrderCounts, OrderCountsSentMessage);
        }
        catch (Exception orderCountsFailure)
        {
            orderCountsMonitoring.RecordOperationResult(OperationResults.Failed);
            return UseCaseFailureDataMessageMapper.MapUseCaseFailureToDataMessage<SendAnsweredOrderCountsResponse>(orderCountsFailure);
        }
    }
}
