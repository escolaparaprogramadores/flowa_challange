using System.Diagnostics;

namespace Base.OrderAccumulator.Entrypoint.Errors;

// Gives every HTTP error the trace id the support finds in the Datadog APM, and writes its log line inside that trace.
// The ASP.NET request Activity has an id of its own, which the Datadog tracer does not use; a span of an ActivitySource,
// though, is born inside the trace the tracer gave the request.
public static class HttpErrorTraceScope
{
    public const string HttpErrorTraceSourceName = "Base.OrderAccumulator.HttpErrors";

    private static readonly ActivitySource HttpErrorTraceSource = new(HttpErrorTraceSourceName);

    // Returns the trace id the answer carries.
    public static string WriteUnderHttpErrorTrace(HttpContext httpContext, Action writeHttpErrorLog)
    {
        using var httpErrorSpan = HttpErrorTraceSource.StartActivity("http.error");
        writeHttpErrorLog();
        if (httpErrorSpan is not null)
            return httpErrorSpan.TraceId.ToString();

        // Without the tracer nobody listens to the source: the trace is the one of the request.
        return Activity.Current is { IdFormat: ActivityIdFormat.W3C } requestActivity ? requestActivity.TraceId.ToString() : httpContext.TraceIdentifier;
    }
}
