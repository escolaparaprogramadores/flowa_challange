using System.Diagnostics;

namespace Base.OrderGenerator.Entrypoint.Errors;

// Gives every HTTP error the trace id the support finds in the Datadog APM, and writes its log line inside that trace.
// The ASP.NET request Activity has an id of its own, which the Datadog tracer does not use; a span of an ActivitySource,
// though, is born inside the trace the tracer gave the request (the same way the order span gets the ClOrdID).
public static class HttpErrorTraceScope
{
    public const string HttpErrorTraceSourceName = "Base.OrderGenerator.HttpErrors";

    private static readonly ActivitySource HttpErrorTraceSource = new(HttpErrorTraceSourceName);

    // Returns the trace id the answer carries. orderClOrdId is the ClOrdID of a failed order, when there is one.
    public static string WriteUnderHttpErrorTrace(HttpContext httpContext, string? orderClOrdId, Action writeHttpErrorLog)
    {
        using var httpErrorSpan = HttpErrorTraceSource.StartActivity("http.error");
        if (httpErrorSpan is not null)
        {
            writeHttpErrorLog();
            return httpErrorSpan.TraceId.ToString();
        }

        // Without the tracer nobody listens to the source. The ClOrdID of an order is then a random GUID (decision 21),
        // and the log line of that order is still written under it (CA-8).
        if (orderClOrdId is not null)
        {
            WriteUnderOrderTraceWithoutTracer(orderClOrdId, writeHttpErrorLog);
            return orderClOrdId;
        }

        writeHttpErrorLog();
        return Activity.Current is { IdFormat: ActivityIdFormat.W3C } requestActivity ? requestActivity.TraceId.ToString() : httpContext.TraceIdentifier;
    }

    private static void WriteUnderOrderTraceWithoutTracer(string orderClOrdId, Action writeHttpErrorLog)
    {
        var requestActivity = Activity.Current;
        var orderFailureLogActivity = new Activity("order-failure-log")
            .SetParentId(ActivityTraceId.CreateFromString(orderClOrdId), ActivitySpanId.CreateRandom())
            .Start();
        try
        {
            writeHttpErrorLog();
        }
        finally
        {
            orderFailureLogActivity.Stop();
            Activity.Current = requestActivity;
        }
    }
}
