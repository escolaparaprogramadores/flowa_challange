using Base.OrderAccumulator.Application.Orders.DecideIncomingOrder;
using Base.OrderAccumulator.Domain.Orders;
using Flowa.Shared.Fix;
using QuickFix.Fields;
using QuickFix.FIX44;
using QuickFix;
using Message = QuickFix.Message;

namespace Base.OrderAccumulator.Entrypoint.Fix;

// Receives the NewOrderSingle, hands it to the order use case and answers with the ExecutionReport.
// Field validation (D-13), the limit and the repeated order (D-11) live in DecideIncomingOrderUseCase.
public sealed class NewOrderSingleConsumer(IServiceScopeFactory orderOperationScopeFactory, ILogger<NewOrderSingleConsumer> orderFixLogger)
    : MessageCracker, IApplication
{
    public void FromApp(Message fixMessage, SessionID fixSessionId) => Crack(fixMessage, fixSessionId);

    public void OnMessage(NewOrderSingle newOrderSingle, SessionID fixSessionId)
    {
        var receivedTraceParent = newOrderSingle.IsSetField(FixOrderTraceProvider.TraceParentTag)
            ? newOrderSingle.GetString(FixOrderTraceProvider.TraceParentTag)
            : null;
        using var orderReceiving = FixOrderTraceProvider.StartOrderReceiving(receivedTraceParent);

        var incomingOrder = new IncomingOrder(
            newOrderSingle.ClOrdID.Value, newOrderSingle.Symbol.Value, newOrderSingle.Side.Value, newOrderSingle.OrderQty.Value, newOrderSingle.Price.Value);

        // QuickFIX calls each session on its own thread and waits for the return; waiting here
        // keeps the answers in the same order as the received orders.
        DecideIncomingOrderOutput orderDecision;
        try
        {
            orderDecision = DecideIncomingOrderInOwnScopeAsync(incomingOrder).GetAwaiter().GetResult();
        }
        catch (Exception orderDecisionException)
        {
            // Single error point of this entry. Without an answer, the OrderGenerator gives up after 5 s and shows
            // communication_error (contract, section 3). The use case stores in a single transaction
            // (DecideIncomingOrderUseCase), so the failure leaves no half-stored order; the FIX session stays up.
            orderFixLogger.LogError(orderDecisionException, "Falha ao processar a ordem {ClOrdId}; nenhum ExecutionReport enviado.", incomingOrder.ClOrdId);
            return;
        }

        if (orderDecision.IsRepeat)
            orderFixLogger.LogInformation("ClOrdID {ClOrdId} repetido: devolvendo a resposta original.", orderDecision.ClOrdId);

        // The order is already stored. If the session dropped before the answer, resending the same ClOrdID returns
        // the stored answer (D-11); the warning makes the case visible in the log.
        if (!Session.SendToTarget(BuildExecutionReport(orderDecision), fixSessionId))
            orderFixLogger.LogWarning("ExecutionReport da ordem {ClOrdId} não foi enviado: a sessão FIX não está logada.", orderDecision.ClOrdId);
    }

    // Each order gets its own unit of work (one database connection), as an HTTP request would.
    private async Task<DecideIncomingOrderOutput> DecideIncomingOrderInOwnScopeAsync(IncomingOrder incomingOrder)
    {
        await using var orderOperationScope = orderOperationScopeFactory.CreateAsyncScope();
        return await orderOperationScope.ServiceProvider.GetRequiredService<DecideIncomingOrderUseCase>().DecideIncomingOrderAsync(incomingOrder);
    }

    // Tags and values from the ExecutionReport table in docs/contracts/contracts.md, section 2.
    public static ExecutionReport BuildExecutionReport(DecideIncomingOrderOutput orderDecision)
    {
        var executionReport = new ExecutionReport(
            new OrderID(orderDecision.OrderId),
            new ExecID(orderDecision.ExecId),
            new ExecType(orderDecision.Accepted ? ExecType.NEW : ExecType.REJECTED),
            new OrdStatus(orderDecision.Accepted ? OrdStatus.NEW : OrdStatus.REJECTED),
            // The FIX 4.4 dictionary requires tag 55 on NewOrderSingle, so the symbol always came.
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
