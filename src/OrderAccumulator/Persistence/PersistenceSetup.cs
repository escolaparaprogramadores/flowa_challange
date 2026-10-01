using Dapper;
using Flowa.Shared;
using Npgsql;
using OrderAccumulator.Exposure;

namespace OrderAccumulator.Persistence;

// Pontos de entrada para o Program.cs: registrar os serviços e criar as tabelas na subida.
public static class PersistenceSetup
{
    private const string SeedExposuresSql = """
        INSERT INTO exposures (symbol)
        SELECT unnest(@Symbols)
        ON CONFLICT (symbol) DO NOTHING
        """;

    public static IServiceCollection AddOrderAccumulatorPersistence(
        this IServiceCollection services, string connectionString)
    {
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton<IOrderProcessor, PostgresOrderProcessor>();
        services.AddSingleton<IExposureReader, PostgresExposureReader>();
        return services;
    }

    // Duas instâncias subindo juntas disputariam o CREATE TABLE IF NOT EXISTS, que não é seguro em paralelo
    // no PostgreSQL; a trava faz uma esperar a outra terminar.
    private const string LockSchemaSql = "SELECT pg_advisory_xact_lock(hashtext('flowa-orderaccumulator-schema'))";

    // Cria o que faltar e garante uma linha zerada por símbolo. Não mexe em exposição já gravada.
    public static async Task ApplySchemaAsync(this NpgsqlDataSource dataSource, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(LockSchemaSql, transaction: transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(ReadSchema(), transaction: transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            SeedExposuresSql, new { Symbols = OrderRules.AllowedOrderSymbols.ToArray() }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    private static string ReadSchema()
    {
        using var stream = typeof(PersistenceSetup).Assembly.GetManifestResourceStream("OrderAccumulator.Persistence.Schema.sql")
            ?? throw new InvalidOperationException("O script Schema.sql não foi embutido no assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
