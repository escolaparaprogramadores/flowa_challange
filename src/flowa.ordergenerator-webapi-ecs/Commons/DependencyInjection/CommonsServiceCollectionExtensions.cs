using Flowa.OrderGenerator.Commons.Logging;
using Flowa.OrderGenerator.Commons.Observability;

namespace Flowa.OrderGenerator.Commons.DependencyInjection;

public static class CommonsServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddApplicationLogger()
        {
            services.AddSingleton(typeof(IApplicationLogger<>), typeof(ApplicationLogger<>));
            return services;
        }

        public IServiceCollection AddOperationMonitoring()
        {
            services.AddSingleton<IOperationMonitoring, ActiveSpanOperationMonitoring>();
            return services;
        }
    }
}
