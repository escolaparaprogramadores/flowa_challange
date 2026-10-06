using System.Reflection;
using System.Text.Json.Serialization;
using Base.OrderAccumulator.Application.Exposures.Interfaces;
using Base.OrderAccumulator.Application.Exposures.UseCases;
using Base.OrderAccumulator.Application.Orders.UseCases;
using Base.OrderAccumulator.Commons.Database;
using Base.OrderAccumulator.Commons.Logging;
using Base.OrderAccumulator.Domain.DomainServices;
using Base.OrderAccumulator.Entrypoint.BackgroundService;
using Base.OrderAccumulator.Entrypoint.ErrorHandling;
using Base.OrderAccumulator.Entrypoint.Exposures.Endpoints;
using Base.OrderAccumulator.Entrypoint.Fix;
using Base.OrderAccumulator.Entrypoint.Orders.Endpoints;
using Base.OrderAccumulator.Infrastructure.DependencyInjection;
using Base.OrderAccumulator.Infrastructure.Exposures.Adapters;
using Base.OrderAccumulator.Infrastructure.Fix;
using Microsoft.Extensions.DependencyInjection.Extensions;

var orderAccumulatorWebBuilder = WebApplication.CreateBuilder(args);
orderAccumulatorWebBuilder.AddApplicationLogging();

var buildCommitSha = ReadBuildCommitSha() is { Length: 40 } shaFromBuild
    ? shaFromBuild
    : throw new InvalidOperationException(
        "The build did not record the commit. Build inside the git repository or pass -p:SourceRevisionId=<full sha>.");

var flowaConnectionString = orderAccumulatorWebBuilder.Configuration.GetConnectionString(OrderAccumulatorConfigurationKeys.OrderDatabaseConnectionStringName)
    ?? throw new InvalidOperationException("Set ConnectionStrings__Flowa to the PostgreSQL connection.");
orderAccumulatorWebBuilder.Services.AddOrderAccumulatorPersistence(flowaConnectionString);
orderAccumulatorWebBuilder.Services.AddOrderMetrics(orderAccumulatorWebBuilder.Configuration);
orderAccumulatorWebBuilder.Services.AddSingleton<ISymbolExposureMemoryPort, InMemorySymbolExposureAdapter>();
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

await orderAccumulatorApp.Services.GetRequiredService<IDatabaseConnectionSource>().ApplyOrderAccumulatorSchemaAsync();
await orderAccumulatorApp.Services.LoadSymbolExposureMemoryAsync();

orderAccumulatorApp.MapGet("/health", () => "Healthy");
orderAccumulatorApp.MapGet("/version", () => new { commit = buildCommitSha });
orderAccumulatorApp.MapExposuresEndpoints();
orderAccumulatorApp.MapOrdersEndpoints();

orderAccumulatorApp.Run();

static string? ReadBuildCommitSha()
{
    var informationalVersion = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    var commitSeparatorIndex = informationalVersion?.IndexOf('+') ?? -1;
    return commitSeparatorIndex >= 0 ? informationalVersion![(commitSeparatorIndex + 1)..] : null;
}

public partial class Program;
