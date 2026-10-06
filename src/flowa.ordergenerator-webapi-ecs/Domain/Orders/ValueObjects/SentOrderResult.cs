using Flowa.OrderGenerator.Domain.Orders.Enums;
using Flowa.OrderGenerator.Domain.Orders.Exceptions;

namespace Flowa.OrderGenerator.Domain.Orders.ValueObjects;

public sealed record SentOrderResult
{
    public const string AcceptedOrderMessage = "Ordem aceita.";
    public const string RejectedOrderWithoutTextMessage = "Ordem rejeitada.";
    public const string AcceptedOrderStatusCode = "accepted";
    public const string RejectedOrderStatusCode = "rejected";

    public SentOrderResult(SentOrderStatus status, string clOrdId, string? orderId = null, string? execId = null, string? rejectionText = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(clOrdId);

        Status = status;
        ClOrdId = clOrdId;
        OrderId = orderId;
        ExecId = execId;
        RejectionText = rejectionText;
    }

    public SentOrderStatus Status { get; }
    public string ClOrdId { get; }
    public string? OrderId { get; }
    public string? ExecId { get; }
    public string? RejectionText { get; }

    public bool IsAccepted => Status == SentOrderStatus.Accepted;

    public void ConfirmOrderWasAnswered()
    {
        switch (Status)
        {
            case SentOrderStatus.Accepted or SentOrderStatus.Rejected:
                return;
            case SentOrderStatus.RejectedByFixReject:
                throw new OrderRejectedByFixRejectException(ClOrdId, RejectionText ?? RejectedOrderWithoutTextMessage);
            case SentOrderStatus.NoLoggedOnSession:
                throw new OrderNotAnsweredException(ClOrdId, OrderNotAnsweredException.FixSessionNotLoggedOnErrorCode);
            case SentOrderStatus.FixSessionLost:
                throw new OrderNotAnsweredException(ClOrdId, OrderNotAnsweredException.FixSessionLostErrorCode);
            case SentOrderStatus.ExecutionReportTimeout:
                throw new OrderNotAnsweredException(ClOrdId, OrderNotAnsweredException.ExecutionReportTimeoutErrorCode);
            default:
                throw new UnexpectedExecutionReportException(ClOrdId);
        }
    }

    public string DescribeOrderAnswer() => IsAccepted ? AcceptedOrderMessage : RejectionText ?? RejectedOrderWithoutTextMessage;

    public string DescribeOrderStatusCode() => IsAccepted ? AcceptedOrderStatusCode : RejectedOrderStatusCode;
}
