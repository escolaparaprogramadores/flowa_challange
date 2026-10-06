using Base.OrderAccumulator.Application.Orders.Responses;
using Base.OrderAccumulator.Application.Orders.UseCases;
using Base.OrderAccumulator.Commons.Logging;
using Base.OrderAccumulator.Commons.Responses;
using Base.OrderAccumulator.Domain.Orders.Enums;
using Base.OrderAccumulator.Domain.Orders.ValueObjects;
using Base.OrderAccumulator.Infrastructure.Fix;
using QuickFix.FIX44;
using QuickFix.Fields;
using QuickFix;
using Message = QuickFix.Message;

namespace Base.OrderAccumulator.Entrypoint.Fix;

public sealed class NewOrderSingleConsumer : MessageCracker, IApplication
{
    private const string ExecutionReportNotSentErrorCode = "execution_report_not_sent";
    private const string UnexpectedErrorCode = "error";

    private readonly IServiceScopeFactory orderOperationScopeFactory;
    private readonly IApplicationLogger<NewOrderSingleConsumer> orderFixLogger;

    public NewOrderSingleConsumer(IServiceScopeFactory orderOperationScopeFactory, IApplicationLogger<NewOrderSingleConsumer> orderFixLogger)
    {
        this.orderOperationScopeFactory = orderOperationScopeFactory ?? throw new ArgumentNullException(nameof(orderOperationScopeFactory));
        this.orderFixLogger = orderFixLogger ?? throw new ArgumentNullException(nameof(orderFixLogger));
    }

    public void FromApp(Message fixMessage, SessionID fixSessionId) => Crack(fixMessage, fixSessionId);

    public void OnMessage(NewOrderSingle newOrderSingle, SessionID fixSessionId)
    {
        var receivedTraceParent = newOrderSingle.IsSetField(FixOrderTraceProvider.TraceParentTag)
            ? newOrderSingle.GetString(FixOrderTraceProvider.TraceParentTag)
            : null;
        using var orderReceiving = FixOrderTraceProvider.StartOrderReceiving(receivedTraceParent);

        var receivedClOrdId = newOrderSingle.ClOrdID.Value;
        var receivedOrderSymbol = newOrderSingle.Symbol.Value;
        var receivedOrderSide = newOrderSingle.Side.Value;
        var receivedOrderQuantity = newOrderSingle.OrderQty.Value;
        var receivedOrderPrice = newOrderSingle.Price.Value;

        DataMessage<DecideIncomingOrderResponse> orderDecisionMessage;
        try
        {
            var incomingOrder = new IncomingOrder(receivedClOrdId, receivedOrderSymbol, receivedOrderSide, receivedOrderQuantity, receivedOrderPrice);
            orderDecisionMessage = DecideIncomingOrderInOwnScopeAsync(incomingOrder).GetAwaiter().GetResult();
        }
        catch (Exception orderDecisionException)
        {
            LogOrderDecisionFailure(orderDecisionException);
            return;
        }

        if (orderDecisionMessage.UnexpectedFailure is { } orderDecisionFailure)
        {
            LogOrderDecisionFailure(orderDecisionFailure);
            return;
        }

        var orderDecision = orderDecisionMessage.Data!;
        LogOrderDecision(orderDecision);

        if (!Session.SendToTarget(BuildExecutionReport(orderDecision), fixSessionId))
            orderFixLogger.LogWarning("ExecutionReport not sent: the FIX session is not logged on.", new { ErrorCode = ExecutionReportNotSentErrorCode });
    }

    private void LogOrderDecisionFailure(Exception orderDecisionFailure) =>
        orderFixLogger.LogError(orderDecisionFailure, "Order decision failed; no ExecutionReport sent.", new { ErrorCode = UnexpectedErrorCode });

    private void LogOrderDecision(DecideIncomingOrderResponse orderDecision)
    {
        if (orderDecision.IsRepeat)
            orderFixLogger.LogInformation("Repeated ClOrdID: sending the stored answer back.");
        else if (orderDecision.DecisionOutcome == OrderDecisionOutcome.RejectedForInvalidFields)
            orderFixLogger.LogWarning("Order rejected: invalid fields.", new { ErrorCode = OrderRejectionErrorCodes.InvalidOrderFields });
        else if (orderDecision.DecisionOutcome == OrderDecisionOutcome.RejectedOverExposureLimit)
            orderFixLogger.LogWarning("Order rejected: exposure limit exceeded.", new { ErrorCode = OrderRejectionErrorCodes.ExposureLimitExceeded });
    }

    private async Task<DataMessage<DecideIncomingOrderResponse>> DecideIncomingOrderInOwnScopeAsync(IncomingOrder incomingOrder)
    {
        await using var orderOperationScope = orderOperationScopeFactory.CreateAsyncScope();
        return await orderOperationScope.ServiceProvider.GetRequiredService<DecideIncomingOrderUseCase>().DecideIncomingOrderAsync(incomingOrder);
    }

    public static ExecutionReport BuildExecutionReport(DecideIncomingOrderResponse orderDecision)
    {
        ArgumentNullException.ThrowIfNull(orderDecision);

        var executionReport = new ExecutionReport(
            new OrderID(orderDecision.OrderId),
            new ExecID(orderDecision.ExecId),
            new ExecType(orderDecision.Accepted ? ExecType.NEW : ExecType.REJECTED),
            new OrdStatus(orderDecision.Accepted ? OrdStatus.NEW : OrdStatus.REJECTED),
            new Symbol(orderDecision.Symbol!),
            new Side(orderDecision.Side),
            new LeavesQty(orderDecision.LeavesQuantity),
            new CumQty(0m),
            new AvgPx(0m))
        {
            ClOrdID = new ClOrdID(orderDecision.ClOrdId)
        };

        if (orderDecision.RejectReason is { } orderRejectReason)
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
