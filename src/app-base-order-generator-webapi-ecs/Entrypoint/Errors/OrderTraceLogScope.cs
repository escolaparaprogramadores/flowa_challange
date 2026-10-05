using System.Diagnostics;

namespace Base.OrderGenerator.Entrypoint.Errors;

// The log line of a failed order is written with its ClOrdID as trace id (CA-8). When the error reaches the
// GlobalErrorHandler the order span already ended, and the ASP.NET request Activity has an id of its own, which is
// not the trace the Datadog tracer gave the order.
public static class OrderTraceLogScope
{
    public static void WriteUnderOrderTrace(string clOrdId, Action writeOrderFailureLog)
    {
        var clOrdIdIsTraceId = clOrdId.Length == 32 && clOrdId.All(char.IsAsciiHexDigitLower);
        if (!clOrdIdIsTraceId)
        {
            writeOrderFailureLog();
            return;
        }

        var requestActivity = Activity.Current;
        var orderFailureLogActivity = new Activity("order-failure-log")
            .SetParentId(ActivityTraceId.CreateFromString(clOrdId), ActivitySpanId.CreateRandom())
            .Start();
        try
        {
            writeOrderFailureLog();
        }
        finally
        {
            orderFailureLogActivity.Stop();
            Activity.Current = requestActivity;
        }
    }
}
