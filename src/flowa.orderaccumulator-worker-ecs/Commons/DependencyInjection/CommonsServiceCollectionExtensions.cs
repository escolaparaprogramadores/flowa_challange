using Flowa.OrderAccumulator.Commons.Database;
using Flowa.OrderAccumulator.Commons.Logging;
using Flowa.OrderAccumulator.Commons.Observability;

namespace Flowa.OrderAccumulator.Commons.DependencyInjection;

public static class CommonsServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationLogger(this IServiceCollection appServices) =>
        appServices.AddSingleton(typeof(IApplicationLogger<>), typeof(ApplicationLogger<>));

    public static IServiceCollection AddOperationMonitoring(this IServiceCollection appServices) =>
        appServices.AddSingleton<IOperationMonitoring, ActiveSpanOperationMonitoring>();

    public static IServiceCollection AddPostgresDatabase(this IServiceCollection appServices, string databaseConnectionString)
    {
        appServices.AddSingleton<IDatabaseConnectionSource>(_ => new PostgresConnectionSource(databaseConnectionString));
        appServices.AddScoped<DatabaseUnitOfWork>();
        appServices.AddScoped<IUnitOfWork>(operationServices => operationServices.GetRequiredService<DatabaseUnitOfWork>());
        appServices.AddScoped<IDatabase, DapperDatabase>();
        return appServices;
    }
}
