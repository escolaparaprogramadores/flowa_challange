namespace Flowa.OrderGenerator.Domain.Orders.Enums;

public enum SentOrderStatus
{
    Accepted,
    Rejected,
    NoLoggedOnSession,
    ExecutionReportTimeout,
    UnexpectedExecutionReport
}
