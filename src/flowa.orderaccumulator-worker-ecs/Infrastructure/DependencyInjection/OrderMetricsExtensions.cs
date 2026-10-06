using Flowa.OrderAccumulator.Application.Orders.Interfaces;
using Flowa.OrderAccumulator.Commons.Observability;
using Flowa.OrderAccumulator.Infrastructure.Orders.Adapters;
using Flowa.OrderAccumulator.Infrastructure.Orders.Options;

namespace Flowa.OrderAccumulator.Infrastructure.DependencyInjection;

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
}
