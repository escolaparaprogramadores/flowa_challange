using Base.OrderAccumulator.Application.Exposures.GetExposures;
using Base.OrderAccumulator.Application.Exposures;
using Base.OrderAccumulator.Commons;
using StatsdClient;

namespace Base.OrderAccumulator.Infrastructure.Metrics;

public static class OrderMetricsExtensions
{
    // The Datadog agent runs as a sidecar in the same task (F5 contract).
    public const string DatadogAgentHost = "localhost";
    public const int DatadogAgentDogStatsdPort = 8125;

    public static IServiceCollection AddOrderMetrics(this IServiceCollection orderAccumulatorServices, IConfiguration orderAccumulatorConfiguration)
    {
        orderAccumulatorServices.AddSingleton<IDogStatsd>(_ =>
            CreateOrderMetricsClient(DatadogAgentDogStatsdPort, orderAccumulatorConfiguration));
        orderAccumulatorServices.AddSingleton<IOrderMetricsPort, DatadogOrderMetricsAdapter>();
        return orderAccumulatorServices;
    }

    // env, service and version come from the DD_* variables the task defines; outside AWS they have no value.
    public static DogStatsdService CreateOrderMetricsClient(int dogStatsdPort, IConfiguration orderAccumulatorConfiguration)
    {
        var orderMetricsClient = new DogStatsdService();
        orderMetricsClient.Configure(
            new StatsdConfig
            {
                StatsdServerName = DatadogAgentHost,
                StatsdPort = dogStatsdPort,
                Environment = orderAccumulatorConfiguration[OrderAccumulatorConfigurationKeys.DatadogEnvironment],
                ServiceName = orderAccumulatorConfiguration[OrderAccumulatorConfigurationKeys.DatadogService],
                ServiceVersion = orderAccumulatorConfiguration[OrderAccumulatorConfigurationKeys.DatadogVersion]
            },
            IgnoreDogStatsdSendFailure);
        return orderMetricsClient;
    }

    // A single read at startup, before the FIX acceptor opens; after that the gauge only reads memory.
    public static async Task LoadSymbolExposureMemoryAsync(this IServiceProvider orderAccumulatorServiceProvider, CancellationToken cancellationToken = default)
    {
        var storedSymbolExposures = await orderAccumulatorServiceProvider.GetRequiredService<ISymbolExposureReadRepository>().GetSymbolExposuresAsync(cancellationToken);
        orderAccumulatorServiceProvider.GetRequiredService<SymbolExposureMemoryService>().LoadStoredExposures(storedSymbolExposures);
    }

    // With no agent listening (local, tests, agent turned off) UDP fails on every send. The metric is
    // secondary: the order goes on and the log does not gain one line per order.
    private static void IgnoreDogStatsdSendFailure(Exception dogStatsdSendFailure) { }
}
