using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Serialization;
using Base.OrderGenerator.Application.Exposures.UseCases;
using Base.OrderGenerator.Application.Orders.UseCases;
using Base.OrderGenerator.Commons.DependencyInjection;
using Base.OrderGenerator.Commons.Logging;
using Base.OrderGenerator.Commons.Observability;
using Base.OrderGenerator.Entrypoint.DependencyInjection;
using Base.OrderGenerator.Entrypoint.ErrorHandling;
using Base.OrderGenerator.Entrypoint.Exposures.Endpoints;
using Base.OrderGenerator.Entrypoint.Orders.Endpoints;
using Base.OrderGenerator.Infrastructure.DependencyInjection;

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
orderGeneratorBuilder.Services.AddSingleton<DistributedContextPropagator>(new IncomingTraceContextIgnoringPropagator());

orderGeneratorBuilder.Services.AddOrderGeneratorInfrastructure();
orderGeneratorBuilder.Services.AddScoped<SendOrderUseCase>();
orderGeneratorBuilder.Services.AddScoped<GetExposuresUseCase>();
orderGeneratorBuilder.Services.AddScoped<ListOrdersUseCase>();
orderGeneratorBuilder.Services.AddScoped<DeleteAllOrdersUseCase>();

orderGeneratorBuilder.Services.ConfigureHttpJsonOptions(jsonOptions => jsonOptions.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
orderGeneratorBuilder.Services.AddSingleton<IProblemDetailsWriter, AnyClientProblemDetailsWriter>();
orderGeneratorBuilder.Services.AddProblemDetails(problemDetailsOptions => problemDetailsOptions.CustomizeProblemDetails = ApiProblemDetailsExtensions.CompleteProblemDetails);
orderGeneratorBuilder.Services.AddExceptionHandler<OrderGeneratorExceptionHandler>();

var orderGeneratorApp = orderGeneratorBuilder.Build();

orderGeneratorApp.UseExceptionHandler();
orderGeneratorApp.UseStatusCodePages();

orderGeneratorApp.UseDefaultFiles();
orderGeneratorApp.UseStaticFiles();
orderGeneratorApp.MapOrdersEndpoints();
orderGeneratorApp.MapExposuresEndpoints();
orderGeneratorApp.MapGet("/health", () => Results.Text("Healthy"));
orderGeneratorApp.MapGet("/version", () => Results.Json(new { commit = buildCommitSha }));
orderGeneratorApp.Map("/api/{**unknownApiPath}", () => Results.NotFound());
orderGeneratorApp.MapFallbackToFile("index.html");

orderGeneratorApp.Run();

static string? ReadBuildCommitSha()
{
    var informationalVersion = typeof(Program).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    var commitSeparatorIndex = informationalVersion?.IndexOf('+') ?? -1;
    return commitSeparatorIndex < 0 ? null : informationalVersion![(commitSeparatorIndex + 1)..];
}

public partial class Program;
