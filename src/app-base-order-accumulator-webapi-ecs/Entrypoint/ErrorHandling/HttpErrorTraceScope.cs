using System.Diagnostics;

namespace Base.OrderAccumulator.Entrypoint.ErrorHandling;

public static class HttpErrorTraceScope
{
    public const string HttpErrorTraceSourceName = "Base.OrderAccumulator.HttpErrors";

    private static readonly ActivitySource HttpErrorTraceSource = new(HttpErrorTraceSourceName);

    public static string WriteUnderHttpErrorTrace(HttpContext httpContext, Action writeHttpErrorLog)
    {
        using var httpErrorSpan = HttpErrorTraceSource.StartActivity("http.error");
        writeHttpErrorLog();
        if (httpErrorSpan is not null)
            return httpErrorSpan.TraceId.ToString();

        return Activity.Current is { IdFormat: ActivityIdFormat.W3C } requestActivity ? requestActivity.TraceId.ToString() : httpContext.TraceIdentifier;
    }
}
