using Flowa.OrderAccumulator.Application.Orders.Responses;
using Flowa.OrderAccumulator.Application.Orders.UseCases;
using Flowa.Commons.Logging;
using Flowa.Commons.Responses;
using Flowa.OrderAccumulator.Domain.Orders.Enums;
using Flowa.OrderAccumulator.Domain.Orders.ValueObjects;
using Flowa.OrderAccumulator.Infrastructure.Fix;
using Flowa.OrderAccumulator.Infrastructure.Orders.Options;
using QuickFix.FIX44;
using QuickFix.Fields;
using QuickFix;
using Message = QuickFix.Message;

namespace Flowa.OrderAccumulator.Entrypoint.Fix;

public sealed class NewOrderSingleConsumer : MessageCracker, IApplication
{
    public const string InternalFailureRejectReason = "Ordem rejeitada: o OrderAccumulator não conseguiu decidir a ordem agora. Tente de novo.";
    public const int DefaultOrderDecisionTimeoutSeconds = 4;

    private const string ExecutionReportNotSentErrorCode = "execution_report_not_sent";
    private const string UnexpectedErrorCode = "error";
    private const string OrderDecisionTimeoutErrorCode = "order_decision_timeout";
    private const string OrderAcceptedAfterTheDeadlineErrorCode = "order_accepted_after_the_deadline";
    private const string NewExecutionReportIdFormat = "N";
    private const char MissingOrderSide = '0';
    private const decimal ZeroForMissingQuantityOrPriceRejectedByTheFieldRule = 0m;

    private readonly IServiceScopeFactory orderOperationScopeFactory;
    private readonly IApplicationLogger<NewOrderSingleConsumer> orderFixLogger;
    private readonly int orderDecisionTimeoutSeconds;

    public NewOrderSingleConsumer(
        IServiceScopeFactory orderOperationScopeFactory, IApplicationLogger<NewOrderSingleConsumer> orderFixLogger, IConfiguration appConfiguration)
    {
        this.orderOperationScopeFactory = orderOperationScopeFactory ?? throw new ArgumentNullException(nameof(orderOperationScopeFactory));
        this.orderFixLogger = orderFixLogger ?? throw new ArgumentNullException(nameof(orderFixLogger));
        orderDecisionTimeoutSeconds = ReadOrderDecisionTimeoutSeconds(appConfiguration ?? throw new ArgumentNullException(nameof(appConfiguration)));
    }

    public static int ReadOrderDecisionTimeoutSeconds(IConfiguration appConfiguration)
    {
        var configuredOrderDecisionTimeoutSeconds = appConfiguration.GetValue(
            OrderAccumulatorConfigurationKeys.OrderDecisionTimeoutSeconds, DefaultOrderDecisionTimeoutSeconds);
        if (configuredOrderDecisionTimeoutSeconds <= 0)
            throw new InvalidOperationException("Set Orders__DecisionTimeoutSeconds to a number of seconds greater than zero.");
        return configuredOrderDecisionTimeoutSeconds;
    }

    public static string BuildOrderDeadlineRejectReason(int orderDecisionTimeoutSeconds) =>
        $"Ordem rejeitada: o OrderAccumulator não decidiu a ordem em {orderDecisionTimeoutSeconds} s. Tente de novo.";

    public void FromApp(Message fixMessage, SessionID fixSessionId) => Crack(fixMessage, fixSessionId);

    public void OnMessage(NewOrderSingle newOrderSingle, SessionID fixSessionId)
    {
        var receivedTraceParent = newOrderSingle.IsSetField(FixOrderTraceProvider.TraceParentTag)
            ? newOrderSingle.GetString(FixOrderTraceProvider.TraceParentTag)
            : null;
        using var orderReceiving = FixOrderTraceProvider.StartOrderReceiving(receivedTraceParent);

        var receivedClOrdId = newOrderSingle.IsSetClOrdID() ? newOrderSingle.ClOrdID.Value : string.Empty;
        var receivedOrderSymbol = newOrderSingle.IsSetSymbol() ? newOrderSingle.Symbol.Value : string.Empty;
        var receivedOrderSide = newOrderSingle.IsSetSide() ? newOrderSingle.Side.Value : MissingOrderSide;
        var receivedOrderQuantity = newOrderSingle.IsSetOrderQty() ? newOrderSingle.OrderQty.Value : ZeroForMissingQuantityOrPriceRejectedByTheFieldRule;
        var receivedOrderPrice = newOrderSingle.IsSetPrice() ? newOrderSingle.Price.Value : ZeroForMissingQuantityOrPriceRejectedByTheFieldRule;

        var orderExecutionReport = DecideOrderWithinTheDeadline(
            receivedClOrdId, receivedOrderSymbol, receivedOrderSide, receivedOrderQuantity, receivedOrderPrice);

        if (!Session.SendToTarget(orderExecutionReport, fixSessionId))
            orderFixLogger.LogWarning("ExecutionReport not sent: the FIX session is not logged on.", new { ErrorCode = ExecutionReportNotSentErrorCode });
    }

    private ExecutionReport DecideOrderWithinTheDeadline(
        string receivedClOrdId, string receivedOrderSymbol, char receivedOrderSide, decimal receivedOrderQuantity, decimal receivedOrderPrice)
    {
        using var orderDecisionDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(orderDecisionTimeoutSeconds));
        Task<DataMessage<DecideIncomingOrderResponse>>? orderDecisionTask = null;
        try
        {
            var incomingOrder = new IncomingOrder(receivedClOrdId, receivedOrderSymbol, receivedOrderSide, receivedOrderQuantity, receivedOrderPrice);
            orderDecisionTask = DecideIncomingOrderInOwnScopeAsync(incomingOrder, orderDecisionDeadline.Token);
            var orderDecisionMessage = orderDecisionTask.WaitAsync(orderDecisionDeadline.Token).GetAwaiter().GetResult();

            if (orderDecisionMessage.Failure is { } orderDecisionFailure)
            {
                if (orderDecisionFailure is OperationCanceledException && orderDecisionDeadline.IsCancellationRequested)
                    return AnswerOrderDecisionTimeout(receivedClOrdId, receivedOrderSymbol, receivedOrderSide);

                LogOrderDecisionFailure(orderDecisionFailure);
                return BuildRejectedExecutionReport(receivedClOrdId, receivedOrderSymbol, receivedOrderSide, InternalFailureRejectReason);
            }

            var orderDecision = orderDecisionMessage.Data!;
            LogOrderDecision(orderDecision);
            return BuildExecutionReport(orderDecision);
        }
        catch (OperationCanceledException) when (orderDecisionDeadline.IsCancellationRequested && orderDecisionTask is not null)
        {
            _ = LogOrderDecisionFinishedAfterTheDeadlineAsync(orderDecisionTask);
            return AnswerOrderDecisionTimeout(receivedClOrdId, receivedOrderSymbol, receivedOrderSide);
        }
        catch (Exception orderDecisionException)
        {
            LogOrderDecisionFailure(orderDecisionException);
            return BuildRejectedExecutionReport(receivedClOrdId, receivedOrderSymbol, receivedOrderSide, InternalFailureRejectReason);
        }
    }

    private ExecutionReport AnswerOrderDecisionTimeout(string receivedClOrdId, string receivedOrderSymbol, char receivedOrderSide)
    {
        orderFixLogger.LogWarning("Order decision passed the deadline; ExecutionReport Rejected sent.",
            new { ErrorCode = OrderDecisionTimeoutErrorCode, OrderDecisionTimeoutSeconds = orderDecisionTimeoutSeconds });
        return BuildRejectedExecutionReport(
            receivedClOrdId, receivedOrderSymbol, receivedOrderSide, BuildOrderDeadlineRejectReason(orderDecisionTimeoutSeconds));
    }

    private async Task LogOrderDecisionFinishedAfterTheDeadlineAsync(Task<DataMessage<DecideIncomingOrderResponse>> lateOrderDecisionTask)
    {
        await ((Task)lateOrderDecisionTask).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (lateOrderDecisionTask.Exception is { } lateOrderDecisionException)
        {
            orderFixLogger.LogError(lateOrderDecisionException.GetBaseException(), "Order decision failed after the deadline.", new { ErrorCode = UnexpectedErrorCode });
            return;
        }

        var lateOrderDecisionMessage = await lateOrderDecisionTask;
        if (lateOrderDecisionMessage.Data is { IsRepeat: false, Accepted: true } lateAcceptedOrder)
            orderFixLogger.LogError(
                new InvalidOperationException($"The order {lateAcceptedOrder.ClOrdId} was answered Rejected at the deadline and then accepted by the database."),
                "Order answered Rejected at the deadline was accepted later by the database.",
                new { ErrorCode = OrderAcceptedAfterTheDeadlineErrorCode, lateAcceptedOrder.ClOrdId });
        else if (lateOrderDecisionMessage.Failure is { } lateOrderDecisionFailure and not OperationCanceledException)
            orderFixLogger.LogError(lateOrderDecisionFailure, "Order decision failed after the deadline.", new { ErrorCode = UnexpectedErrorCode });
    }

    private void LogOrderDecisionFailure(Exception orderDecisionFailure) =>
        orderFixLogger.LogError(orderDecisionFailure, "Order decision failed; ExecutionReport Rejected sent.", new { ErrorCode = UnexpectedErrorCode });

    private void LogOrderDecision(DecideIncomingOrderResponse orderDecision)
    {
        if (orderDecision.IsRepeat)
            orderFixLogger.LogInformation("Repeated ClOrdID: sending the stored answer back.");
        else if (orderDecision.DecisionOutcome == OrderDecisionOutcome.RejectedForInvalidFields)
            orderFixLogger.LogWarning("Order rejected: invalid fields.", new { ErrorCode = OrderRejectionErrorCodes.InvalidOrderFields });
        else if (orderDecision.DecisionOutcome == OrderDecisionOutcome.RejectedOverExposureLimit)
            orderFixLogger.LogWarning("Order rejected: exposure limit exceeded.", new { ErrorCode = OrderRejectionErrorCodes.ExposureLimitExceeded });
    }

    private async Task<DataMessage<DecideIncomingOrderResponse>> DecideIncomingOrderInOwnScopeAsync(
        IncomingOrder incomingOrder, CancellationToken orderDecisionCancellationToken)
    {
        await using var orderOperationScope = orderOperationScopeFactory.CreateAsyncScope();
        return await orderOperationScope.ServiceProvider.GetRequiredService<DecideIncomingOrderUseCase>()
            .DecideIncomingOrderAsync(incomingOrder, orderDecisionCancellationToken);
    }

    public static ExecutionReport BuildExecutionReport(DecideIncomingOrderResponse orderDecision)
    {
        ArgumentNullException.ThrowIfNull(orderDecision);

        return BuildExecutionReport(
            orderDecision.OrderId, orderDecision.ExecId, orderDecision.Accepted, orderDecision.ClOrdId,
            orderDecision.Symbol!, orderDecision.Side, orderDecision.LeavesQuantity, orderDecision.RejectReason);
    }

    public static ExecutionReport BuildRejectedExecutionReport(
        string receivedClOrdId, string receivedOrderSymbol, char receivedOrderSide, string orderRejectReason) =>
        BuildExecutionReport(
            Guid.NewGuid().ToString(NewExecutionReportIdFormat), Guid.NewGuid().ToString(NewExecutionReportIdFormat), isOrderAccepted: false,
            receivedClOrdId, receivedOrderSymbol, receivedOrderSide, leavesQuantity: 0m, orderRejectReason);

    private static ExecutionReport BuildExecutionReport(
        string orderId, string execId, bool isOrderAccepted, string clOrdId, string orderSymbol, char orderSide, decimal leavesQuantity, string? orderRejectReason)
    {
        var executionReport = new ExecutionReport(
            new OrderID(orderId),
            new ExecID(execId),
            new ExecType(isOrderAccepted ? ExecType.NEW : ExecType.REJECTED),
            new OrdStatus(isOrderAccepted ? OrdStatus.NEW : OrdStatus.REJECTED),
            new Symbol(orderSymbol),
            new Side(orderSide),
            new LeavesQty(leavesQuantity),
            new CumQty(0m),
            new AvgPx(0m))
        {
            ClOrdID = new ClOrdID(clOrdId)
        };

        if (orderRejectReason is not null)
            executionReport.Text = new Text(orderRejectReason);

        return executionReport;
    }

    public void OnCreate(SessionID fixSessionId) { }
    public void OnLogon(SessionID fixSessionId) { }
    public void OnLogout(SessionID fixSessionId) { }
    public void ToAdmin(Message fixMessage, SessionID fixSessionId) { }
    public void FromAdmin(Message fixMessage, SessionID fixSessionId) { }
    public void ToApp(Message fixMessage, SessionID fixSessionId) { }
}
