using System.Reflection;
using Npgsql;
using OrderAccumulator.Exposure;
using OrderAccumulator.Fix;
using OrderAccumulator.Persistence;

var orderAccumulatorWebBuilder = WebApplication.CreateBuilder(args);

// O /version promete o sha completo; sem ele o app não sobe, para o erro aparecer no build e não no aceite.
var buildCommitSha = ReadBuildCommitSha() is { Length: 40 } shaFromBuild
    ? shaFromBuild
    : throw new InvalidOperationException(
        "O build não gravou o commit. Compile dentro do repositório git ou passe -p:SourceRevisionId=<sha completo>.");

var flowaConnectionString = orderAccumulatorWebBuilder.Configuration.GetConnectionString("Flowa")
    ?? throw new InvalidOperationException("Defina ConnectionStrings__Flowa com a conexão do PostgreSQL.");
orderAccumulatorWebBuilder.Services.AddOrderAccumulatorPersistence(flowaConnectionString);

// Acceptor FIX 4.4: sobe junto com o app, depois da migração abaixo.
orderAccumulatorWebBuilder.Services.AddSingleton<OrderFixApplication>();
orderAccumulatorWebBuilder.Services.AddHostedService<FixAcceptorService>();

var orderAccumulatorApp = orderAccumulatorWebBuilder.Build();

// As tabelas precisam existir antes de a primeira ordem chegar.
await orderAccumulatorApp.Services.GetRequiredService<NpgsqlDataSource>().ApplyOrderAccumulatorSchemaAsync();

orderAccumulatorApp.MapGet("/health", () => "Healthy");
orderAccumulatorApp.MapGet("/version", () => new { commit = buildCommitSha });

orderAccumulatorApp.MapGet("/api/exposures", async (IExposureReader exposureReader, CancellationToken cancellationToken) =>
{
    var symbolExposures = await exposureReader.GetSymbolExposuresAsync(cancellationToken);
    return new ExposuresResponse(
        ExposureLimit.PerSymbol,
        symbolExposures.Select(symbolExposure => new ExposureItem(
            symbolExposure.Symbol, symbolExposure.Exposure, symbolExposure.RemainingExposureCapacity)).ToList());
});

orderAccumulatorApp.Run();

// O SDK grava o commit do build na versão informativa ("1.0.0+<sha>").
static string? ReadBuildCommitSha()
{
    var informationalVersion = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    var commitSeparatorIndex = informationalVersion?.IndexOf('+') ?? -1;
    return commitSeparatorIndex >= 0 ? informationalVersion![(commitSeparatorIndex + 1)..] : null;
}

// Corpo do GET /api/exposures (docs/contracts/contracts.md, seção 1). Remaining vira "remaining" no JSON.
public sealed record ExposuresResponse(decimal Limit, IReadOnlyList<ExposureItem> Exposures);

public sealed record ExposureItem(string Symbol, decimal Exposure, decimal Remaining);

public partial class Program;
