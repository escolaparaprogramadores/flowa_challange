using System.Reflection;
using Npgsql;
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

var orderAccumulatorApp = orderAccumulatorWebBuilder.Build();

// As tabelas precisam existir antes de a primeira ordem chegar.
await orderAccumulatorApp.Services.GetRequiredService<NpgsqlDataSource>().ApplyOrderAccumulatorSchemaAsync();

orderAccumulatorApp.MapGet("/health", () => "Healthy");
orderAccumulatorApp.MapGet("/version", () => new { commit = buildCommitSha });

orderAccumulatorApp.Run();

// O SDK grava o commit do build na versão informativa ("1.0.0+<sha>").
static string? ReadBuildCommitSha()
{
    var informationalVersion = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    var commitSeparatorIndex = informationalVersion?.IndexOf('+') ?? -1;
    return commitSeparatorIndex >= 0 ? informationalVersion![(commitSeparatorIndex + 1)..] : null;
}

public partial class Program;
