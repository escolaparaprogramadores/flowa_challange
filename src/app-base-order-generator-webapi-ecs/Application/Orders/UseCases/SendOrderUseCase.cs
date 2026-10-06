using Base.OrderGenerator.Application.Orders.Interfaces;
using Base.OrderGenerator.Commons.Responses;
using Base.OrderGenerator.Domain.Orders.Enums;
using Base.OrderGenerator.Domain.Orders.Exceptions;
using Base.OrderGenerator.Domain.Orders.ValueObjects;

namespace Base.OrderGenerator.Application.Orders.UseCases;

public sealed class SendOrderUseCase(IOrderAccumulatorPort orderAccumulatorPort)
{
    public const string AcceptedOrderMessage = "Ordem aceita.";
    public const string RejectedOrderWithoutTextMessage = "Ordem rejeitada.";
    public const string FixSessionNotLoggedOnErrorCode = "fix-session-not-logged-on";
    public const string ExecutionReportTimeoutErrorCode = "execution-report-timeout";

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
