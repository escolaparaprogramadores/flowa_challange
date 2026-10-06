using System.Reflection;
using System.Text.Json.Serialization;
using Flowa.OrderAccumulator.Application.Exposures.UseCases;
using Flowa.OrderAccumulator.Application.Orders.UseCases;
using Flowa.OrderAccumulator.Commons.Database;
using Flowa.OrderAccumulator.Commons.DependencyInjection;
using Flowa.OrderAccumulator.Commons.Logging;
using Flowa.OrderAccumulator.Domain.DomainServices;
using Flowa.OrderAccumulator.Entrypoint.BackgroundService;
using Flowa.OrderAccumulator.Entrypoint.ErrorHandling;
using Flowa.OrderAccumulator.Entrypoint.Exposures.Endpoints;
using Flowa.OrderAccumulator.Entrypoint.Fix;
using Flowa.OrderAccumulator.Entrypoint.Logging;
using Flowa.OrderAccumulator.Entrypoint.Orders.Endpoints;
using Flowa.OrderAccumulator.Infrastructure.DependencyInjection;
using Flowa.OrderAccumulator.Infrastructure.Fix;
using Flowa.OrderAccumulator.Infrastructure.Orders.Options;
using Microsoft.Extensions.DependencyInjection.Extensions;

var orderAccumulatorWebBuilder = WebApplication.CreateBuilder(args);
orderAccumulatorWebBuilder.Logging.AddJsonLogsWithTraceId();
orderAccumulatorWebBuilder.Services.AddApplicationLogger();
orderAccumulatorWebBuilder.Services.AddOperationMonitoring();

var buildCommitSha = ReadBuildCommitSha() is { Length: 40 } shaFromBuild
    ? shaFromBuild
    : throw new InvalidOperationException(
        "The build did not record the commit. Build inside the git repository or pass -p:SourceRevisionId=<full sha>.");

var flowaConnectionString = orderAccumulatorWebBuilder.Configuration.GetConnectionString(OrderAccumulatorConfigurationKeys.OrderDatabaseConnectionStringName)
    ?? throw new InvalidOperationException("Set ConnectionStrings__Flowa to the PostgreSQL connection.");
orderAccumulatorWebBuilder.Services.AddOrderAccumulatorInfrastructure(flowaConnectionString, orderAccumulatorWebBuilder.Configuration);
orderAccumulatorWebBuilder.Services.TryAddSingleton(TimeProvider.System);
orderAccumulatorWebBuilder.Services.AddScoped<OrderDecisionDomainService>();
orderAccumulatorWebBuilder.Services.AddScoped<DecideIncomingOrderUseCase>();
orderAccumulatorWebBuilder.Services.AddScoped<DeleteAllOrdersUseCase>();
orderAccumulatorWebBuilder.Services.AddScoped<ListOrdersUseCase>();
orderAccumulatorWebBuilder.Services.AddScoped<GetExposuresUseCase>();
orderAccumulatorWebBuilder.Services.AddHostedService<SymbolExposureGaugeBackgroundService>();

orderAccumulatorWebBuilder.Services.AddSingleton<FixSessionLogFactory>();
orderAccumulatorWebBuilder.Services.AddSingleton<NewOrderSingleConsumer>();
orderAccumulatorWebBuilder.Services.AddHostedService<FixAcceptorBackgroundService>();

orderAccumulatorWebBuilder.Services.ConfigureHttpJsonOptions(jsonOptions => jsonOptions.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
orderAccumulatorWebBuilder.Services.AddSingleton<IProblemDetailsWriter, ProblemDetailsForAnyClientWriter>();
orderAccumulatorWebBuilder.Services.AddProblemDetails(problemDetailsOptions => problemDetailsOptions.CustomizeProblemDetails = ApiProblemDetailsExtensions.CompleteProblemDetails);
orderAccumulatorWebBuilder.Services.AddExceptionHandler<GlobalErrorHandler>();

var orderAccumulatorApp = orderAccumulatorWebBuilder.Build();
orderAccumulatorApp.UseExceptionHandler();
orderAccumulatorApp.UseStatusCodePages();
orderAccumulatorApp.UseMiddleware<RequestReceivedLoggingMiddleware>();

await orderAccumulatorApp.Services.GetRequiredService<IDatabaseConnectionSource>().ApplyOrderAccumulatorSchemaAsync();
await orderAccumulatorApp.Services.LoadSymbolExposureMemoryAsync();

orderAccumulatorApp.MapGet("/health", () => "Healthy");
orderAccumulatorApp.MapGet("/version", () => new { commit = buildCommitSha });
orderAccumulatorApp.MapExposuresEndpoints();
orderAccumulatorApp.MapOrdersEndpoints();

orderAccumulatorApp.Lifetime.ApplicationStarted.Register(() =>
    orderAccumulatorApp.Services.GetRequiredService<IApplicationLogger<Program>>()
        .LogInformation("Application started.", new { Environment = orderAccumulatorApp.Environment.EnvironmentName, BuildCommitSha = buildCommitSha }));

orderAccumulatorApp.Run();

static string? ReadBuildCommitSha()
{
    var informationalVersion = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    var commitSeparatorIndex = informationalVersion?.IndexOf('+') ?? -1;
    return commitSeparatorIndex >= 0 ? informationalVersion![(commitSeparatorIndex + 1)..] : null;
}

public partial class Program;
