using Base.OrderGenerator.Commons.Logging;

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
    }
}
