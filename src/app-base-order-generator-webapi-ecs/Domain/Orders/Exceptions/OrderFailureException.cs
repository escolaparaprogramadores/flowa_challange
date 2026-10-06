namespace Base.OrderGenerator.Domain.Orders.Exceptions;

public abstract class OrderFailureException(string clOrdId, string failureMessage) : Exception(failureMessage)
{
    public string ClOrdId { get; } = clOrdId;
}

public sealed class OrderNotAnsweredException(string clOrdId, string errorCode)
    : OrderFailureException(clOrdId, "The OrderAccumulator did not answer the order.")
{
    public string ErrorCode { get; } = errorCode;
}

public sealed class UnexpectedExecutionReportException(string clOrdId)
    : OrderFailureException(clOrdId, "The OrderAccumulator answered with an ExecutionReport that is neither New nor Rejected.");
