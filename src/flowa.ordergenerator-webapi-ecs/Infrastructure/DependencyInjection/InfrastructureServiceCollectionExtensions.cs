using Flowa.Commons.DependencyInjection;
using Flowa.OrderGenerator.Application.Exposures.Interfaces;
using Flowa.OrderGenerator.Application.Orders.Interfaces;
using Flowa.OrderGenerator.Infrastructure.Exposures.Repositories;
using Flowa.OrderGenerator.Infrastructure.Fix;
using Flowa.OrderGenerator.Infrastructure.Orders.Options;
using Flowa.OrderGenerator.Infrastructure.Orders.Repositories;

namespace Flowa.OrderGenerator.Infrastructure.DependencyInjection;

internal static class InfrastructureServiceCollectionExtensions
{
    public const string OrderDatabaseConnectionStringName = "Flowa";

    extension(IServiceCollection services)
    {
        public IServiceCollection AddOrderGeneratorInfrastructure(IConfiguration orderGeneratorConfiguration)
        {
            services.AddOptions<FixOptions>().BindConfiguration(FixOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();

            services.AddSingleton<FixSessionLogFactory>();
            services.AddSingleton<FixOrderClient>();
            services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<FixOrderClient>());
            services.AddSingleton<IOrderAccumulatorPort>(serviceProvider => serviceProvider.GetRequiredService<FixOrderClient>());

            var orderDatabaseConnectionString = orderGeneratorConfiguration.GetConnectionString(OrderDatabaseConnectionStringName)
                ?? throw new InvalidOperationException("Set ConnectionStrings__Flowa to the PostgreSQL connection.");
            services.AddPostgresDatabase(orderDatabaseConnectionString, orderGeneratorConfiguration);
            services.AddScoped<ISymbolExposureRepository, SymbolExposureRepository>();
            services.AddScoped<IStoredOrderRepository, StoredOrderRepository>();
            return services;
        }
    }
}
