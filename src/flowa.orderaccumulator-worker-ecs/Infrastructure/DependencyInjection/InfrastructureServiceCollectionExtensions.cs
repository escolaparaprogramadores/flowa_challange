using Flowa.OrderAccumulator.Application.Exposures.Interfaces;
using Flowa.OrderAccumulator.Infrastructure.Exposures.Adapters;

namespace Flowa.OrderAccumulator.Infrastructure.DependencyInjection;

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
