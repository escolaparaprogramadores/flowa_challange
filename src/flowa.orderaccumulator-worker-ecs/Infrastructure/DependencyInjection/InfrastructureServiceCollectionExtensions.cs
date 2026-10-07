namespace Flowa.OrderAccumulator.Infrastructure.DependencyInjection;

internal static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddOrderAccumulatorInfrastructure(
        this IServiceCollection orderAccumulatorServices, string orderDatabaseConnectionString, IConfiguration orderAccumulatorConfiguration)
    {
        orderAccumulatorServices.AddOrderAccumulatorPersistence(orderDatabaseConnectionString, orderAccumulatorConfiguration);
        return orderAccumulatorServices;
    }
}
