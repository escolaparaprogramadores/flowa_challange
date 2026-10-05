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
    public const string UnexpectedExecutionReportMessage = "The OrderAccumulator answered with an ExecutionReport that is neither New nor Rejected.";

    // A rejected order is an answer of the OrderAccumulator, so it is a success with its tag 58 text (decision 13).
    public async Task<DataMessage<SentOrderResult>> SendOrderAsync(OrderToSend orderToSend)
    {
        var sentOrderResult = await orderAccumulatorPort.SendOrderAsync(orderToSend);
        return sentOrderResult.Status switch
        {
            SentOrderStatus.Accepted => DataMessage<SentOrderResult>.CreateSuccessMessage(sentOrderResult, AcceptedOrderMessage),
            SentOrderStatus.Rejected => DataMessage<SentOrderResult>.CreateSuccessMessage(
                sentOrderResult, sentOrderResult.RejectionText ?? RejectedOrderWithoutTextMessage),
            SentOrderStatus.NoLoggedOnSession => DataMessage<SentOrderResult>.CreateErrorMessage(
                OrderAccumulatorMessages.OrderAccumulatorUnavailableMessage, ResultStatus.ServiceUnavailable, errorCode: FixSessionNotLoggedOnErrorCode),
            SentOrderStatus.ExecutionReportTimeout => DataMessage<SentOrderResult>.CreateErrorMessage(
                OrderAccumulatorMessages.OrderAccumulatorUnavailableMessage, ResultStatus.ServiceUnavailable, errorCode: ExecutionReportTimeoutErrorCode),
            _ => DataMessage<SentOrderResult>.CreateErrorMessage(UnexpectedExecutionReportMessage, ResultStatus.InternalError)
        };
    }
}
