namespace Flowa.OrderGenerator.Domain.Orders.Enums;

public enum SentOrderStatus
{
    Accepted,
    Rejected,
    RejectedByFixReject,
    NoLoggedOnSession,
    FixSessionLost,
    ExecutionReportTimeout,
    UnexpectedExecutionReport
}
