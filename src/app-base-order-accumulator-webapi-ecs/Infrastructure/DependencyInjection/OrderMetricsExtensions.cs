using Base.OrderAccumulator.Application.Exposures.Interfaces;
using Base.OrderAccumulator.Application.Orders.Interfaces;
using Base.OrderAccumulator.Commons.Observability;
using Base.OrderAccumulator.Infrastructure.Orders.Adapters;
using Base.OrderAccumulator.Infrastructure.Orders.Options;

namespace Base.OrderAccumulator.Infrastructure.DependencyInjection;

public static class OrderMetricsExtensions
{
    public const string DatadogAgentHost = "localhost";
    public const int DatadogAgentDogStatsdPort = 8125;

    public static IServiceCollection AddOrderMetrics(this IServiceCollection orderAccumulatorServices, IConfiguration orderAccumulatorConfiguration)
    {
        orderAccumulatorServices.AddSingleton<IMetricsClient>(_ =>
            CreateOrderMetricsClient(DatadogAgentDogStatsdPort, orderAccumulatorConfiguration));
        orderAccumulatorServices.AddSingleton<IOrderMetricsPort, DatadogOrderMetricsAdapter>();
        return orderAccumulatorServices;
    }

    public static DogStatsdMetricsClient CreateOrderMetricsClient(int dogStatsdPort, IConfiguration orderAccumulatorConfiguration) =>
        new(
            DatadogAgentHost,
            dogStatsdPort,
            orderAccumulatorConfiguration[OrderAccumulatorConfigurationKeys.DatadogEnvironment],
            orderAccumulatorConfiguration[OrderAccumulatorConfigurationKeys.DatadogService],
            orderAccumulatorConfiguration[OrderAccumulatorConfigurationKeys.DatadogVersion]);

    public static async Task LoadSymbolExposureMemoryAsync(this IServiceProvider orderAccumulatorServiceProvider, CancellationToken cancellationToken = default)
    {
        await using var exposureReadScope = orderAccumulatorServiceProvider.CreateAsyncScope();
        var storedSymbolExposures = await exposureReadScope.ServiceProvider.GetRequiredService<ISymbolExposureReadRepository>().GetSymbolExposuresAsync(cancellationToken);
        orderAccumulatorServiceProvider.GetRequiredService<ISymbolExposureMemoryPort>().LoadStoredExposures(storedSymbolExposures);
    }
}
