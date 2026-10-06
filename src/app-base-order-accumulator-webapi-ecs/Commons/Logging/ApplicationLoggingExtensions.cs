using System.Text.Encodings.Web;
using System.Text.Json;

namespace Base.OrderAccumulator.Commons.Logging;

public static class ApplicationLoggingExtensions
{
    public static ILoggingBuilder AddJsonLogsWithTraceId(this ILoggingBuilder appLogging)
    {
        appLogging.ClearProviders();
        appLogging.SetMinimumLevel(LogLevel.Information);
        appLogging.Configure(loggerFactoryOptions =>
            loggerFactoryOptions.ActivityTrackingOptions = ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);
        appLogging.AddJsonConsole(jsonConsoleOptions =>
        {
            jsonConsoleOptions.IncludeScopes = true;
            jsonConsoleOptions.UseUtcTimestamp = true;
            jsonConsoleOptions.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
            jsonConsoleOptions.JsonWriterOptions = new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        });
        return appLogging;
    }
}
