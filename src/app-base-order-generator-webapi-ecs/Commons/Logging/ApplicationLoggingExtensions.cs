using System.Text.Encodings.Web;
using System.Text.Json;

namespace Base.OrderGenerator.Commons.Logging;

public static class ApplicationLoggingExtensions
{
    extension(ILoggingBuilder loggingBuilder)
    {
        public ILoggingBuilder AddJsonLogsWithTraceId()
        {
            loggingBuilder.ClearProviders();
            loggingBuilder.SetMinimumLevel(LogLevel.Information);
            loggingBuilder.Configure(loggerFactoryOptions =>
                loggerFactoryOptions.ActivityTrackingOptions = ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);
            loggingBuilder.AddJsonConsole(jsonConsoleOptions =>
            {
                jsonConsoleOptions.IncludeScopes = true;
                jsonConsoleOptions.UseUtcTimestamp = true;
                jsonConsoleOptions.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
                jsonConsoleOptions.JsonWriterOptions = new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            });
            return loggingBuilder;
        }
    }
}
