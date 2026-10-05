using Base.OrderGenerator.Commons;
using Base.OrderGenerator.Domain.Orders;

namespace Base.OrderGenerator.Application.Orders.SendOrder;

// Sends an order whose format was checked to the OrderAccumulator and returns its answer.
public sealed class SendOrderUseCase(IOrderAccumulatorPort orderAccumulatorPort)
{
    public const string AcceptedOrderMessage = "Ordem aceita.";
    public const string RejectedOrderWithoutTextMessage = "Ordem rejeitada.";
    public const string FixSessionNotLoggedOnErrorCode = "fix-session-not-logged-on";
    public const string ExecutionReportTimeoutErrorCode = "execution-report-timeout";

    // A rejected order is an answer of the OrderAccumulator, so it is a success with its tag 58 text (decision 13).
    // An order with no answer already has its ClOrdID: the failure goes to the GlobalErrorHandler with it.
    public async Task<DataMessage<SentOrderResult>> SendOrderAsync(OrderToSend orderToSend)
    {
        var sentOrderResult = await orderAccumulatorPort.SendOrderAsync(orderToSend);
        return sentOrderResult.Status switch
        {
            SentOrderStatus.Accepted => DataMessage<SentOrderResult>.CreateSuccessMessage(sentOrderResult, AcceptedOrderMessage),
            SentOrderStatus.Rejected => DataMessage<SentOrderResult>.CreateSuccessMessage(
                sentOrderResult, sentOrderResult.RejectionText ?? RejectedOrderWithoutTextMessage),
            SentOrderStatus.NoLoggedOnSession => throw new OrderNotAnsweredException(sentOrderResult.ClOrdId, FixSessionNotLoggedOnErrorCode),
            SentOrderStatus.ExecutionReportTimeout => throw new OrderNotAnsweredException(sentOrderResult.ClOrdId, ExecutionReportTimeoutErrorCode),
            _ => throw new UnexpectedExecutionReportException(sentOrderResult.ClOrdId)
        };
    }
}
