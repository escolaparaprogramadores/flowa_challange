using Flowa.OrderGenerator.Application.Exposures.Interfaces;
using Flowa.OrderGenerator.Application.Orders.Interfaces;
using Flowa.OrderGenerator.Commons.Http;
using Flowa.OrderGenerator.Infrastructure.Exposures.Adapters;
using Flowa.OrderGenerator.Infrastructure.Fix;
using Flowa.OrderGenerator.Infrastructure.Orders.Adapters;
using Flowa.OrderGenerator.Infrastructure.Orders.Options;
using Microsoft.Extensions.Options;

namespace Flowa.OrderGenerator.Infrastructure.DependencyInjection;

internal static class InfrastructureServiceCollectionExtensions
{
    public static readonly TimeSpan OrderAccumulatorRequestTimeout = TimeSpan.FromSeconds(5);

    extension(IServiceCollection services)
    {
        public IServiceCollection AddOrderGeneratorInfrastructure()
        {
            services.AddOptions<FixOptions>().BindConfiguration(FixOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
            services.AddOptions<OrderAccumulatorOptions>().BindConfiguration(OrderAccumulatorOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();

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
        serviceProvider.GetRequiredService<IOptions<OrderAccumulatorOptions>>().Value.BaseUrl;
}
