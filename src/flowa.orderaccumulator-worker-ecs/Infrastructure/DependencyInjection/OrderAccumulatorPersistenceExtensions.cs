using Flowa.OrderAccumulator.Application.Exposures.Interfaces;
using Flowa.OrderAccumulator.Application.Orders.Interfaces;
using Flowa.Commons.Database;
using Flowa.Commons.DependencyInjection;
using Flowa.OrderAccumulator.Domain.Exposures.Interfaces;
using Flowa.OrderAccumulator.Domain.Orders.Interfaces;
using Flowa.OrderAccumulator.Domain.Orders.ValueObjects;
using Flowa.OrderAccumulator.Infrastructure.Exposures.Repositories;
using Flowa.OrderAccumulator.Infrastructure.Orders.Repositories;

namespace Flowa.OrderAccumulator.Infrastructure.DependencyInjection;

public static class OrderAccumulatorPersistenceExtensions
{
    private const string SeedExposuresSql = """
        INSERT INTO exposures (symbol)
        SELECT unnest(@Symbols)
        ON CONFLICT (symbol) DO NOTHING
        """;

    public static IServiceCollection AddOrderAccumulatorPersistence(
        this IServiceCollection orderAccumulatorServices, string orderDatabaseConnectionString)
    {
        orderAccumulatorServices.AddPostgresDatabase(orderDatabaseConnectionString);
        orderAccumulatorServices.AddScoped<IOrderRepository, OrderRepository>();
        orderAccumulatorServices.AddScoped<IExposureRepository, ExposureRepository>();
        orderAccumulatorServices.AddScoped<ISymbolExposureReadRepository, SymbolExposureReadRepository>();
        orderAccumulatorServices.AddScoped<IOrderListReadRepository, OrderListReadRepository>();
        return orderAccumulatorServices;
    }

    private const string LockSchemaSql = "SELECT pg_advisory_xact_lock(hashtext('flowa-orderaccumulator-schema'))";

    public static async Task ApplyOrderAccumulatorSchemaAsync(this IDatabaseConnectionSource orderDatabaseConnectionSource, CancellationToken cancellationToken = default)
    {
        await using var schemaUnitOfWork = new DatabaseUnitOfWork(orderDatabaseConnectionSource);
        var schemaDatabase = new DapperDatabase(schemaUnitOfWork);
        await schemaUnitOfWork.BeginTransactionAsync(cancellationToken);
        await schemaDatabase.ExecuteSqlCommandAsync(LockSchemaSql, null, cancellationToken);
        await schemaDatabase.ExecuteSqlCommandAsync(ReadOrderAccumulatorSchema(), null, cancellationToken);
        await schemaDatabase.ExecuteSqlCommandAsync(
            SeedExposuresSql, new { Symbols = OrderFieldPolicy.AllowedOrderSymbols.ToArray() }, cancellationToken);
        await schemaUnitOfWork.CommitTransactionAsync(cancellationToken);
    }

    private static string ReadOrderAccumulatorSchema()
    {
        using var schemaResourceStream = typeof(OrderAccumulatorPersistenceExtensions).Assembly.GetManifestResourceStream("Flowa.OrderAccumulator.Infrastructure.Persistence.Schema.sql")
            ?? throw new InvalidOperationException("The Schema.sql script was not embedded in the assembly.");
        using var schemaReader = new StreamReader(schemaResourceStream);
        return schemaReader.ReadToEnd();
    }
}
