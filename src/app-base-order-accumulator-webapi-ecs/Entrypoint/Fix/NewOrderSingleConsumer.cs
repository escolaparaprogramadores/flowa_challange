using Base.OrderAccumulator.Application.Orders.Responses;
using Base.OrderAccumulator.Application.Orders.UseCases;
using Base.OrderAccumulator.Commons.Logging;
using Base.OrderAccumulator.Domain.Orders.ValueObjects;
using Base.OrderAccumulator.Infrastructure.Fix;
using QuickFix.FIX44;
using QuickFix.Fields;
using QuickFix;
using Message = QuickFix.Message;

namespace Base.OrderAccumulator.Entrypoint.Fix;

public sealed class NewOrderSingleConsumer(IServiceScopeFactory orderOperationScopeFactory, IApplicationLogger<NewOrderSingleConsumer> orderFixLogger)
    : MessageCracker, IApplication
{
    private const string InvalidOrderFieldsErrorCode = "invalid_order_fields";
    private const string ExposureLimitExceededErrorCode = "exposure_limit_exceeded";
    private const string ExecutionReportNotSentErrorCode = "execution_report_not_sent";
    private const string UnexpectedErrorCode = "error";

    public void FromApp(Message fixMessage, SessionID fixSessionId) => Crack(fixMessage, fixSessionId);

    public void OnMessage(NewOrderSingle newOrderSingle, SessionID fixSessionId)
    {
        var receivedTraceParent = newOrderSingle.IsSetField(FixOrderTraceProvider.TraceParentTag)
            ? newOrderSingle.GetString(FixOrderTraceProvider.TraceParentTag)
            : null;
        using var orderReceiving = FixOrderTraceProvider.StartOrderReceiving(receivedTraceParent);

        var incomingOrder = new IncomingOrder(
            newOrderSingle.ClOrdID.Value, newOrderSingle.Symbol.Value, newOrderSingle.Side.Value, newOrderSingle.OrderQty.Value, newOrderSingle.Price.Value);

        DecideIncomingOrderResponse orderDecision;
        try
        {
            orderDecision = DecideIncomingOrderInOwnScopeAsync(incomingOrder).GetAwaiter().GetResult();
        }
        catch (Exception orderDecisionException)
        {
            orderFixLogger.LogError(orderDecisionException, "Order decision failed; no ExecutionReport sent.", new { ErrorCode = UnexpectedErrorCode });
            return;
        }

        LogOrderDecision(orderDecision);

        if (!Session.SendToTarget(BuildExecutionReport(orderDecision), fixSessionId))
            orderFixLogger.LogWarning("ExecutionReport not sent: the FIX session is not logged on.", new { ErrorCode = ExecutionReportNotSentErrorCode });
    }

    private void LogOrderDecision(DecideIncomingOrderResponse orderDecision)
    {
        if (orderDecision.IsRepeat)
            orderFixLogger.LogInformation("Repeated ClOrdID: sending the stored answer back.");
        else if (orderDecision is { Accepted: false, RejectedForInvalidFields: true })
            orderFixLogger.LogWarning("Order rejected: invalid fields.", new { ErrorCode = InvalidOrderFieldsErrorCode });
        else if (!orderDecision.Accepted)
            orderFixLogger.LogWarning("Order rejected: exposure limit exceeded.", new { ErrorCode = ExposureLimitExceededErrorCode });
    }

    private async Task<DecideIncomingOrderResponse> DecideIncomingOrderInOwnScopeAsync(IncomingOrder incomingOrder)
    {
        await using var orderOperationScope = orderOperationScopeFactory.CreateAsyncScope();
        return await orderOperationScope.ServiceProvider.GetRequiredService<DecideIncomingOrderUseCase>().DecideIncomingOrderAsync(incomingOrder);
    }

    public static ExecutionReport BuildExecutionReport(DecideIncomingOrderResponse orderDecision)
    {
        var executionReport = new ExecutionReport(
            new OrderID(orderDecision.OrderId),
            new ExecID(orderDecision.ExecId),
            new ExecType(orderDecision.Accepted ? ExecType.NEW : ExecType.REJECTED),
            new OrdStatus(orderDecision.Accepted ? OrdStatus.NEW : OrdStatus.REJECTED),
            new Symbol(orderDecision.Symbol!),
            new Side(orderDecision.Side),
            new LeavesQty(orderDecision.Accepted ? orderDecision.Quantity : 0m),
            new CumQty(0m),
            new AvgPx(0m))
        {
            ClOrdID = new ClOrdID(orderDecision.ClOrdId)
        };

        if (!orderDecision.Accepted)
            executionReport.Text = new Text(orderDecision.RejectReason!);

        return executionReport;
    }

    public void OnCreate(SessionID fixSessionId) { }
    public void OnLogon(SessionID fixSessionId) { }
    public void OnLogout(SessionID fixSessionId) { }
    public void ToAdmin(Message fixMessage, SessionID fixSessionId) { }
    public void FromAdmin(Message fixMessage, SessionID fixSessionId) { }
    public void ToApp(Message fixMessage, SessionID fixSessionId) { }
}
