using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Serialization;
using Flowa.OrderGenerator.Application.Exposures.UseCases;
using Flowa.OrderGenerator.Application.Orders.UseCases;
using Flowa.Commons.DependencyInjection;
using Flowa.Commons.Logging;
using Flowa.Commons.Observability;
using Flowa.OrderGenerator.Entrypoint.DependencyInjection;
using Flowa.OrderGenerator.Entrypoint.ErrorHandling;
using Flowa.OrderGenerator.Entrypoint.Exposures.Endpoints;
using Flowa.OrderGenerator.Entrypoint.Logging;
using Flowa.OrderGenerator.Entrypoint.Observability;
using Flowa.OrderGenerator.Entrypoint.Orders.Endpoints;
using Flowa.OrderGenerator.Infrastructure.DependencyInjection;

var buildCommitSha = ReadBuildCommitSha() is { Length: 40 } shaFromBuild
    ? shaFromBuild
    : throw new InvalidOperationException(
        "The build did not record the commit. Build inside the git repository or pass -p:SourceRevisionId=<full sha>.");

var orderGeneratorBuilder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
orderGeneratorBuilder.UseDefaultHttpPortWhenMissing();
orderGeneratorBuilder.Logging.AddJsonLogsWithTraceId();
orderGeneratorBuilder.Services.AddApplicationLogger();
orderGeneratorBuilder.Services.AddOperationMonitoring(
    OrderGeneratorUseCaseDurationMetric.MeterName, OrderGeneratorUseCaseDurationMetric.MetricName);
orderGeneratorBuilder.Services.AddSingleton<DistributedContextPropagator>(new IncomingTraceContextIgnoringPropagator());

orderGeneratorBuilder.Services.AddOrderGeneratorInfrastructure();
orderGeneratorBuilder.Services.AddScoped<SendOrderUseCase>();
orderGeneratorBuilder.Services.AddScoped<GetExposuresUseCase>();
orderGeneratorBuilder.Services.AddScoped<ListOrdersUseCase>();
orderGeneratorBuilder.Services.AddScoped<DeleteAllOrdersUseCase>();

orderGeneratorBuilder.Services.ConfigureHttpJsonOptions(jsonOptions => jsonOptions.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
orderGeneratorBuilder.Services.AddSingleton<DataMessageHttpResponseConverter>();
orderGeneratorBuilder.Services.AddSingleton<IProblemDetailsWriter, AnyClientProblemDetailsWriter>();
orderGeneratorBuilder.Services.AddProblemDetails(problemDetailsOptions => problemDetailsOptions.CustomizeProblemDetails = ApiProblemDetailsExtensions.CompleteProblemDetails);
orderGeneratorBuilder.Services.AddExceptionHandler<OrderGeneratorExceptionHandler>();

var orderGeneratorApp = orderGeneratorBuilder.Build();

orderGeneratorApp.UseExceptionHandler();
orderGeneratorApp.UseStatusCodePages();
orderGeneratorApp.UseMiddleware<RequestReceivedLoggingMiddleware>();

orderGeneratorApp.UseDefaultFiles();
orderGeneratorApp.UseStaticFiles();
orderGeneratorApp.MapOrdersEndpoints();
orderGeneratorApp.MapExposuresEndpoints();
orderGeneratorApp.MapGet("/health", () => Results.Text("Healthy"));
orderGeneratorApp.MapGet("/version", () => Results.Json(new { commit = buildCommitSha }));
orderGeneratorApp.Map("/api/{**unknownApiPath}", () => Results.NotFound());
orderGeneratorApp.MapFallbackToFile("index.html");

orderGeneratorApp.Lifetime.ApplicationStarted.Register(() =>
    orderGeneratorApp.Services.GetRequiredService<IApplicationLogger<Program>>()
        .LogInformation("Application started.", new { Environment = orderGeneratorApp.Environment.EnvironmentName, BuildCommitSha = buildCommitSha }));

orderGeneratorApp.Run();

static string? ReadBuildCommitSha()
{
    var informationalVersion = typeof(Program).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    var commitSeparatorIndex = informationalVersion?.IndexOf('+') ?? -1;
    return commitSeparatorIndex < 0 ? null : informationalVersion![(commitSeparatorIndex + 1)..];
}

public partial class Program;
