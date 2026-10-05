namespace Base.OrderGenerator.Commons;

// An order that failed after its ClOrdID existed. The HTTP answer and its log line carry that ClOrdID as trace id
// (CA-11, CA-8): it is the trace the Datadog tracer gave the order, not the id of the ASP.NET request Activity.
public abstract class OrderFailureException(string clOrdId, string failureMessage) : Exception(failureMessage)
{
    public string ClOrdId { get; } = clOrdId;
}

// No FIX session, or no ExecutionReport in 5 s: the OrderAccumulator did not answer the order (the 503).
public sealed class OrderNotAnsweredException(string clOrdId, string errorCode)
    : OrderFailureException(clOrdId, "The OrderAccumulator did not answer the order.")
{
    public string ErrorCode { get; } = errorCode;
}

// The OrderAccumulator answered with an ExecutionReport the contract does not have (the 500).
public sealed class UnexpectedExecutionReportException(string clOrdId)
    : OrderFailureException(clOrdId, "The OrderAccumulator answered with an ExecutionReport that is neither New nor Rejected.");
