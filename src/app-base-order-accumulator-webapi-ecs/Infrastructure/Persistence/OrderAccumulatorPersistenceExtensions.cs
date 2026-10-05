using Base.OrderAccumulator.Application.Exposures.GetExposures;
using Base.OrderAccumulator.Application.Orders.ListOrders;
using Base.OrderAccumulator.Commons;
using Base.OrderAccumulator.Domain.Exposures;
using Base.OrderAccumulator.Domain.Orders;
using Dapper;
using Flowa.Shared;
using Npgsql;

namespace Base.OrderAccumulator.Infrastructure.Persistence;

// Pontos de entrada para o Program.cs: registrar os serviços e criar as tabelas na subida.
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

    // Duas instâncias subindo juntas disputariam o CREATE TABLE IF NOT EXISTS, que não é seguro em paralelo
    // no PostgreSQL; a trava faz uma esperar a outra terminar.
    private const string LockSchemaSql = "SELECT pg_advisory_xact_lock(hashtext('flowa-orderaccumulator-schema'))";

    // Cria o que faltar e garante uma linha zerada por símbolo. Não mexe em exposição já gravada.
    public static async Task ApplyOrderAccumulatorSchemaAsync(this NpgsqlDataSource orderDatabaseDataSource, CancellationToken cancellationToken = default)
    {
        await using var orderDatabaseConnection = await orderDatabaseDataSource.OpenConnectionAsync(cancellationToken);
        await using var orderDatabaseTransaction = await orderDatabaseConnection.BeginTransactionAsync(cancellationToken);
        await orderDatabaseConnection.ExecuteAsync(new CommandDefinition(LockSchemaSql, transaction: orderDatabaseTransaction, cancellationToken: cancellationToken));
        await orderDatabaseConnection.ExecuteAsync(new CommandDefinition(ReadOrderAccumulatorSchema(), transaction: orderDatabaseTransaction, cancellationToken: cancellationToken));
        await orderDatabaseConnection.ExecuteAsync(new CommandDefinition(
            SeedExposuresSql, new { Symbols = OrderRules.AllowedOrderSymbols.ToArray() }, orderDatabaseTransaction, cancellationToken: cancellationToken));
        await orderDatabaseTransaction.CommitAsync(cancellationToken);
    }

    private static string ReadOrderAccumulatorSchema()
    {
        using var schemaResourceStream = typeof(OrderAccumulatorPersistenceExtensions).Assembly.GetManifestResourceStream("Base.OrderAccumulator.Infrastructure.Persistence.Schema.sql")
            ?? throw new InvalidOperationException("O script Schema.sql não foi embutido no assembly.");
        using var schemaReader = new StreamReader(schemaResourceStream);
        return schemaReader.ReadToEnd();
    }
}
