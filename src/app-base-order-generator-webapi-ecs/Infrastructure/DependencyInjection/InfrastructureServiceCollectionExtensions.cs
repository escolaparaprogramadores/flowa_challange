using Base.OrderGenerator.Application.Exposures.Interfaces;
using Base.OrderGenerator.Application.Orders.Interfaces;
using Base.OrderGenerator.Commons.Http;
using Base.OrderGenerator.Infrastructure.Exposures.Adapters;
using Base.OrderGenerator.Infrastructure.Fix;
using Base.OrderGenerator.Infrastructure.Orders.Adapters;
using Base.OrderGenerator.Infrastructure.Orders.Options;

namespace Base.OrderGenerator.Infrastructure.DependencyInjection;

public static class InfrastructureServiceCollectionExtensions
{
    public static readonly TimeSpan OrderAccumulatorRequestTimeout = TimeSpan.FromSeconds(5);

    extension(IServiceCollection services)
    {
        public IServiceCollection AddOrderGeneratorInfrastructure()
        {
            services.AddSingleton<FixSessionLogFactory>();
            services.AddSingleton<FixOrderClient>();
            services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<FixOrderClient>());
            services.AddSingleton<IOrderAccumulatorPort>(serviceProvider => serviceProvider.GetRequiredService<FixOrderClient>());

            services.AddHttpApi(HttpStoredOrdersAdapter.OrderAccumulatorApiName, ReadOrderAccumulatorBaseUrl, OrderAccumulatorRequestTimeout);
            services.AddSingleton<IStoredOrdersPort, HttpStoredOrdersAdapter>();
            services.AddSingleton<ISymbolExposuresPort, HttpSymbolExposuresAdapter>();
            return services;
        }
    }

    private static string ReadOrderAccumulatorBaseUrl(IServiceProvider serviceProvider) =>
        serviceProvider.GetRequiredService<IConfiguration>()[OrderGeneratorConfigurationKeys.OrderAccumulatorBaseUrl]
            ?? throw new InvalidOperationException("Configuration OrderAccumulator:BaseUrl is missing.");
}
