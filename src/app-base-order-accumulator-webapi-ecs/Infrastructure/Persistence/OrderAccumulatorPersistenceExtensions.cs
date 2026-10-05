using Base.OrderAccumulator.Application.Exposures.GetExposures;
using Base.OrderAccumulator.Application.Orders.ListOrders;
using Base.OrderAccumulator.Commons;
using Base.OrderAccumulator.Domain.Exposures;
using Base.OrderAccumulator.Domain.Orders;
using Dapper;
using Npgsql;

namespace Base.OrderAccumulator.Infrastructure.Persistence;

// Entry points for Program.cs: register the services and create the tables at startup.
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
        orderAccumulatorServices.AddSingleton(_ => NpgsqlDataSource.Create(orderDatabaseConnectionString));
        orderAccumulatorServices.AddScoped<PostgresUnitOfWork>();
        orderAccumulatorServices.AddScoped<IUnitOfWork>(orderOperationServices => orderOperationServices.GetRequiredService<PostgresUnitOfWork>());
        orderAccumulatorServices.AddScoped<IOrderRepository, OrderRepository>();
        orderAccumulatorServices.AddScoped<IExposureRepository, ExposureRepository>();
        orderAccumulatorServices.AddSingleton<ISymbolExposureReadRepository, SymbolExposureReadRepository>();
        orderAccumulatorServices.AddSingleton<IOrderListReadRepository, OrderListReadRepository>();
        return orderAccumulatorServices;
    }

    // Two instances starting together would race on CREATE TABLE IF NOT EXISTS, which is not safe in parallel
    // in PostgreSQL; the lock makes one wait for the other to finish.
    private const string LockSchemaSql = "SELECT pg_advisory_xact_lock(hashtext('flowa-orderaccumulator-schema'))";

    // Creates what is missing and ensures one zeroed row per symbol. Does not touch exposure already stored.
    public static async Task ApplyOrderAccumulatorSchemaAsync(this NpgsqlDataSource orderDatabaseDataSource, CancellationToken cancellationToken = default)
    {
        await using var orderDatabaseConnection = await orderDatabaseDataSource.OpenConnectionAsync(cancellationToken);
        await using var orderDatabaseTransaction = await orderDatabaseConnection.BeginTransactionAsync(cancellationToken);
        await orderDatabaseConnection.ExecuteAsync(new CommandDefinition(LockSchemaSql, transaction: orderDatabaseTransaction, cancellationToken: cancellationToken));
        await orderDatabaseConnection.ExecuteAsync(new CommandDefinition(ReadOrderAccumulatorSchema(), transaction: orderDatabaseTransaction, cancellationToken: cancellationToken));
        await orderDatabaseConnection.ExecuteAsync(new CommandDefinition(
            SeedExposuresSql, new { Symbols = OrderFieldRule.AllowedOrderSymbols.ToArray() }, orderDatabaseTransaction, cancellationToken: cancellationToken));
        await orderDatabaseTransaction.CommitAsync(cancellationToken);
    }

    private static string ReadOrderAccumulatorSchema()
    {
        using var schemaResourceStream = typeof(OrderAccumulatorPersistenceExtensions).Assembly.GetManifestResourceStream("Base.OrderAccumulator.Infrastructure.Persistence.Schema.sql")
            ?? throw new InvalidOperationException("The Schema.sql script was not embedded in the assembly.");
        using var schemaReader = new StreamReader(schemaResourceStream);
        return schemaReader.ReadToEnd();
    }
}
