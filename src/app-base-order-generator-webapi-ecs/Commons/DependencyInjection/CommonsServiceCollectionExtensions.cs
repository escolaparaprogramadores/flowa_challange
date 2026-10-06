using Base.OrderGenerator.Commons.Logging;
using Base.OrderGenerator.Commons.Observability;

namespace Base.OrderGenerator.Commons.DependencyInjection;

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
