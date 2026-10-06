using Base.OrderAccumulator.Commons.Database;

namespace Base.OrderAccumulator.Commons.DependencyInjection;

public static class CommonsServiceCollectionExtensions
{
    public static IServiceCollection AddPostgresDatabase(this IServiceCollection appServices, string databaseConnectionString)
    {
        appServices.AddSingleton<IDatabaseConnectionSource>(_ => new PostgresConnectionSource(databaseConnectionString));
        appServices.AddScoped<DatabaseUnitOfWork>();
        appServices.AddScoped<IUnitOfWork>(operationServices => operationServices.GetRequiredService<DatabaseUnitOfWork>());
        appServices.AddScoped<IDatabase, DapperDatabase>();
        return appServices;
    }
}
