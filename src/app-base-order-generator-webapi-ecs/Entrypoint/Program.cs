using Base.OrderGenerator.Application.Exposures.GetExposures;
using Base.OrderGenerator.Application.Orders.DeleteAllOrders;
using Base.OrderGenerator.Application.Orders.ListOrders;
using Base.OrderGenerator.Application.Orders.SendOrder;
using Base.OrderGenerator.Commons;
using Base.OrderGenerator.Entrypoint;
using Base.OrderGenerator.Entrypoint.Errors;
using Base.OrderGenerator.Infrastructure;
using Base.OrderGenerator.Infrastructure.Fix;
using Base.OrderGenerator.Infrastructure.Logging;
using Base.OrderGenerator.Infrastructure.Tracing;
using System.Diagnostics;
using System.Text.Json.Serialization;

// O /version promete o sha completo; sem ele o app não sobe, para o erro aparecer no build e não no aceite.
var buildCommitSha = OrderGeneratorApiEndpoints.ReadBuildCommitSha() is { Length: 40 } shaFromBuild
    ? shaFromBuild
    : throw new InvalidOperationException(
        "O build não gravou o commit. Compile dentro do repositório git ou passe -p:SourceRevisionId=<sha completo>.");

// Raiz na pasta do binário: appsettings.json e wwwroot são achados de qualquer pasta de onde o processo suba.
var orderGeneratorBuilder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
OrderGeneratorHttpPortConfiguration.UseDefaultOrderGeneratorHttpPortWhenMissing(orderGeneratorBuilder);
orderGeneratorBuilder.AddApplicationLogging();
orderGeneratorBuilder.Services.AddSingleton<DistributedContextPropagator>(new IncomingTraceContextIgnoringPropagator());

orderGeneratorBuilder.Services.AddSingleton<FixSessionLogFactory>();
orderGeneratorBuilder.Services.AddSingleton<FixOrderClient>();
orderGeneratorBuilder.Services.AddHostedService(orderGeneratorServices => orderGeneratorServices.GetRequiredService<FixOrderClient>());
orderGeneratorBuilder.Services.AddSingleton<IOrderAccumulatorPort>(orderGeneratorServices => orderGeneratorServices.GetRequiredService<FixOrderClient>());
orderGeneratorBuilder.Services.AddScoped<SendOrderUseCase>();
orderGeneratorBuilder.Services.AddScoped<GetExposuresUseCase>();
orderGeneratorBuilder.Services.AddScoped<ListOrdersUseCase>();
orderGeneratorBuilder.Services.AddScoped<DeleteAllOrdersUseCase>();

orderGeneratorBuilder.Services.AddHttpClient<IOrderAccumulatorHttpClient, OrderAccumulatorHttpClient>(
    OrderAccumulatorHttpClient.OrderAccumulatorHttpClientName, (orderGeneratorServices, accumulatorClient) =>
{
    var accumulatorBaseUrl = orderGeneratorServices.GetRequiredService<IConfiguration>()[OrderGeneratorConfigurationKeys.OrderAccumulatorBaseUrl]
        ?? throw new InvalidOperationException("Configuração OrderAccumulator:BaseUrl ausente.");
    accumulatorClient.BaseAddress = new Uri(accumulatorBaseUrl);
    accumulatorClient.Timeout = TimeSpan.FromSeconds(5);
});

// Success as DataMessage, with the status by name; every error as problem+json with traceId and one log line.
orderGeneratorBuilder.Services.ConfigureHttpJsonOptions(jsonOptions => jsonOptions.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
// The own writer comes before AddProblemDetails: it is the first in line and answers any caller.
orderGeneratorBuilder.Services.AddSingleton<IProblemDetailsWriter, ProblemDetailsForAnyClientWriter>();
orderGeneratorBuilder.Services.AddProblemDetails(problemDetailsOptions => problemDetailsOptions.CustomizeProblemDetails = ApiProblemDetails.CompleteProblemDetails);
orderGeneratorBuilder.Services.AddExceptionHandler<GlobalErrorHandler>();

var orderGeneratorApp = orderGeneratorBuilder.Build();

orderGeneratorApp.UseExceptionHandler();
orderGeneratorApp.UseStatusCodePages();

orderGeneratorApp.UseDefaultFiles();
orderGeneratorApp.UseStaticFiles();
orderGeneratorApp.MapOrderGeneratorRoutes(buildCommitSha);
orderGeneratorApp.MapFallbackToFile("index.html");

orderGeneratorApp.Run();

// Deixa o WebApplicationFactory dos testes enxergar o ponto de entrada.
public partial class Program;
