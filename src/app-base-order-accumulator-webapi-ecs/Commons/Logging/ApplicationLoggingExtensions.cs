using System.Text.Encodings.Web;
using System.Text.Json;

namespace Base.OrderAccumulator.Commons.Logging;

public static class ApplicationLoggingExtensions
{
    public static void AddApplicationLogging(this IHostApplicationBuilder appBuilder)
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
            jsonConsoleOptions.JsonWriterOptions = new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        });
        appBuilder.Services.AddSingleton(typeof(IApplicationLogger<>), typeof(ApplicationLogger<>));
    }
}
