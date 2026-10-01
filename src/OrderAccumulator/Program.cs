using System.Reflection;
using Npgsql;
using OrderAccumulator.Persistence;

var builder = WebApplication.CreateBuilder(args);

// O /version promete o sha completo; sem ele o app não sobe, para o erro aparecer no build e não no aceite.
var buildCommitSha = ReadBuildCommitSha() is { Length: 40 } shaFromBuild
    ? shaFromBuild
    : throw new InvalidOperationException(
        "O build não gravou o commit. Compile dentro do repositório git ou passe -p:SourceRevisionId=<sha completo>.");

var flowaConnectionString = builder.Configuration.GetConnectionString("Flowa")
    ?? throw new InvalidOperationException("Defina ConnectionStrings__Flowa com a conexão do PostgreSQL.");
builder.Services.AddOrderAccumulatorPersistence(flowaConnectionString);

var app = builder.Build();

// As tabelas precisam existir antes de a primeira ordem chegar.
await app.Services.GetRequiredService<NpgsqlDataSource>().ApplySchemaAsync();

app.MapGet("/health", () => "Healthy");
app.MapGet("/version", () => new { commit = buildCommitSha });

app.Run();

// O SDK grava o commit do build na versão informativa ("1.0.0+<sha>").
static string? ReadBuildCommitSha()
{
    var informationalVersion = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    var commitSeparatorIndex = informationalVersion?.IndexOf('+') ?? -1;
    return commitSeparatorIndex >= 0 ? informationalVersion![(commitSeparatorIndex + 1)..] : null;
}

public partial class Program;
