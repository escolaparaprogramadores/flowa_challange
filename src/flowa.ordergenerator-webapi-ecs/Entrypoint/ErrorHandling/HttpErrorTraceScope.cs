using System.Diagnostics;

namespace Flowa.OrderGenerator.Entrypoint.ErrorHandling;

public static class HttpErrorTraceScope
{
    public const string HttpErrorTraceSourceName = "Base.OrderGenerator.HttpErrors";

    private static readonly ActivitySource HttpErrorTraceSource = new(HttpErrorTraceSourceName);

    public static string WriteUnderHttpErrorTrace(HttpContext httpContext, string? orderClOrdId, Action writeHttpErrorLog)
    {
        using var httpErrorSpan = HttpErrorTraceSource.StartActivity("http.error");
        if (httpErrorSpan is not null)
        {
            writeHttpErrorLog();
            return httpErrorSpan.TraceId.ToString();
        }

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
