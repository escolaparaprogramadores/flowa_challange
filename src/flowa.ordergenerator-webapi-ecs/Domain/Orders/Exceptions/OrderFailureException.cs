namespace Flowa.OrderGenerator.Domain.Orders.Exceptions;

public abstract class OrderFailureException(string clOrdId, string errorCode, string failureMessage) : Exception(failureMessage)
{
    public string ClOrdId { get; } = clOrdId;
    public string ErrorCode { get; } = errorCode;
}

public sealed class OrderNotAnsweredException(string clOrdId, string errorCode)
    : OrderFailureException(clOrdId, errorCode, "The OrderAccumulator did not answer the order.")
{
    public const string FixSessionNotLoggedOnErrorCode = "fix-session-not-logged-on";
    public const string ExecutionReportTimeoutErrorCode = "execution-report-timeout";
    public const string FixSessionLostErrorCode = "fix-session-lost";

    public bool OrderMayHaveBeenAccepted => ErrorCode != FixSessionNotLoggedOnErrorCode;
}

public sealed class OrderRejectedByFixRejectException(string clOrdId, string rejectText)
    : OrderFailureException(clOrdId, FixOrderRejectedErrorCode, "The order came back in a FIX Reject or BusinessMessageReject.")
{
    public const string FixOrderRejectedErrorCode = "fix-order-rejected";

    public string RejectText { get; } = rejectText;
}

public sealed class UnexpectedExecutionReportException(string clOrdId)
    : OrderFailureException(clOrdId, UnexpectedExecutionReportErrorCode, "The OrderAccumulator answered with an ExecutionReport that is neither New nor Rejected.")
{
    public const string UnexpectedExecutionReportErrorCode = "internal-error";
}
