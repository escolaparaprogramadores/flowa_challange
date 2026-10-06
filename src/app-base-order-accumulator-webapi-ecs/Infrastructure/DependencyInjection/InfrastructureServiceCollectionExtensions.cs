using Base.OrderAccumulator.Application.Exposures.Interfaces;
using Base.OrderAccumulator.Infrastructure.Exposures.Adapters;

namespace Base.OrderAccumulator.Infrastructure.DependencyInjection;

internal static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddOrderAccumulatorInfrastructure(
        this IServiceCollection orderAccumulatorServices, string orderDatabaseConnectionString, IConfiguration orderAccumulatorConfiguration)
    {
        orderAccumulatorServices.AddOrderAccumulatorPersistence(orderDatabaseConnectionString);
        orderAccumulatorServices.AddOrderMetrics(orderAccumulatorConfiguration);
        orderAccumulatorServices.AddSingleton<ISymbolExposureMemoryPort, InMemorySymbolExposureAdapter>();
        return orderAccumulatorServices;
    }
}
