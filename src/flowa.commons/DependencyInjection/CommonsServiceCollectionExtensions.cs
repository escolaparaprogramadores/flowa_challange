using System.Diagnostics.Metrics;
using Flowa.Commons.Database;
using Flowa.Commons.Logging;
using Flowa.Commons.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Flowa.Commons.DependencyInjection;

public static class CommonsServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddApplicationLogger()
        {
            services.AddSingleton(typeof(IApplicationLogger<>), typeof(ApplicationLogger<>));
            return services;
        }

        public IServiceCollection AddOperationMonitoring(string operationMeterName, string operationDurationMetricName)
        {
            services.AddSingleton<IOperationMonitoring>(monitoringServices => new ActiveSpanOperationMonitoring(
                monitoringServices.GetRequiredService<IMeterFactory>(), operationMeterName, operationDurationMetricName));
            return services;
        }

        public IServiceCollection AddPostgresDatabase(string databaseConnectionString, IConfiguration appConfiguration)
        {
            var pooledConnectionString = PostgresConnectionPool.ApplyMaximumPoolSize(
                databaseConnectionString, appConfiguration[PostgresConnectionPool.MaximumPoolSizeKey]);
            services.AddSingleton<IDatabaseConnectionSource>(_ => new PostgresConnectionSource(pooledConnectionString));
            services.AddScoped<DatabaseUnitOfWork>();
            services.AddScoped<IUnitOfWork>(operationServices => operationServices.GetRequiredService<DatabaseUnitOfWork>());
            services.AddScoped<IDatabase, DapperDatabase>();
            return services;
        }
    }
}
