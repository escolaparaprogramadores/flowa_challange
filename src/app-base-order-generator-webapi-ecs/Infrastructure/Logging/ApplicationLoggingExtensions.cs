using System.Text.Encodings.Web;
using System.Text.Json;
using Base.OrderGenerator.Commons;

namespace Base.OrderGenerator.Infrastructure.Logging;

public static class ApplicationLoggingExtensions
{
    // One JSON line per event on stdout. Inside a span the framework adds TraceId and SpanId to the line
    // (in Scopes); for an order that trace id is its ClOrdID (decision 21). Debug is never written (decision 22).
    public static void AddApplicationLogging(this WebApplicationBuilder appBuilder)
    {
        appBuilder.Logging.ClearProviders();
        appBuilder.Logging.SetMinimumLevel(LogLevel.Information);
        appBuilder.Logging.Configure(loggerFactoryOptions =>
            loggerFactoryOptions.ActivityTrackingOptions = ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);
        appBuilder.Logging.AddJsonConsole(jsonConsoleOptions =>
        {
            jsonConsoleOptions.IncludeScopes = true;
            jsonConsoleOptions.UseUtcTimestamp = true;
            jsonConsoleOptions.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
            // The pt-BR texts (tag 58) stay readable in the log; quotes and control characters are still escaped.
            jsonConsoleOptions.JsonWriterOptions = new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        });
        appBuilder.Services.AddSingleton(typeof(IApplicationLogger<>), typeof(ApplicationLogger<>));
    }
}
