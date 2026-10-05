namespace Base.OrderGenerator.Domain.Orders;

public enum SentOrderStatus
{
    Accepted,
    Rejected,
    NoLoggedOnSession,
    ExecutionReportTimeout,
    UnexpectedExecutionReport
}

// What came back for an order sent to the accumulator. Ids and text only exist when an ExecutionReport arrived.
public sealed record SentOrderResult(SentOrderStatus Status, string ClOrdId, string? OrderId = null, string? ExecId = null, string? RejectionText = null);
