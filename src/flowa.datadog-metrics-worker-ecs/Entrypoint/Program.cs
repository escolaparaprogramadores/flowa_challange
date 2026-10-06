using System.Reflection;
using Flowa.Commons.Logging;
using Flowa.DatadogMetrics.Entrypoint.DependencyInjection;
using Flowa.DatadogMetrics.Infrastructure.DependencyInjection;

var datadogMetricsBuilder = Host.CreateApplicationBuilder(args);
datadogMetricsBuilder.Logging.AddJsonLogsWithTraceId();

var buildCommitSha = ReadBuildCommitSha() is { Length: 40 } shaFromBuild
    ? shaFromBuild
    : throw new InvalidOperationException(
        "The build did not record the commit. Build inside the git repository or pass -p:SourceRevisionId=<full sha>.");

var flowaConnectionString = datadogMetricsBuilder.Configuration.GetConnectionString(DatadogMetricsConfigurationKeys.FlowaConnectionStringName)
    ?? throw new InvalidOperationException("Set ConnectionStrings__Flowa to the PostgreSQL connection.");
datadogMetricsBuilder.Services.AddDatadogMetricsWorker(flowaConnectionString, datadogMetricsBuilder.Configuration);

var datadogMetricsHost = datadogMetricsBuilder.Build();
datadogMetricsHost.Services.GetRequiredService<IApplicationLogger<Program>>()
    .LogInformation("Application started.", new { Environment = datadogMetricsBuilder.Environment.EnvironmentName, BuildCommitSha = buildCommitSha });
datadogMetricsHost.Run();

static string? ReadBuildCommitSha()
{
    var informationalVersion = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    var commitSeparatorIndex = informationalVersion?.IndexOf('+') ?? -1;
    return commitSeparatorIndex >= 0 ? informationalVersion![(commitSeparatorIndex + 1)..] : null;
}

public partial class Program;
