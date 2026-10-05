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

// /version promises the full sha; without it the app does not start, so the error shows up at build time and not at acceptance.
var buildCommitSha = OrderGeneratorApiEndpoints.ReadBuildCommitSha() is { Length: 40 } shaFromBuild
    ? shaFromBuild
    : throw new InvalidOperationException(
        "The build did not record the commit. Build inside the git repository or pass -p:SourceRevisionId=<full sha>.");

// Root in the binary folder: appsettings.json and wwwroot are found from whatever folder the process starts in.
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
        ?? throw new InvalidOperationException("Configuration OrderAccumulator:BaseUrl is missing.");
    accumulatorClient.BaseAddress = new Uri(accumulatorBaseUrl);
    accumulatorClient.Timeout = TimeSpan.FromSeconds(5);
});

// Success as DataMessage, with the status by name; every error as problem+json with traceId and one log line.
orderGeneratorBuilder.Services.ConfigureHttpJsonOptions(jsonOptions => jsonOptions.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
// The own writer comes before AddProblemDetails: it is the first in line and answers any caller.
orderGeneratorBuilder.Services.AddSingleton<IProblemDetailsWriter, ProblemDetailsForAnyClientWriter>();
orderGeneratorBuilder.Services.AddProblemDetails(problemDetailsOptions => problemDetailsOptions.CustomizeProblemDetails = ApiProblemDetailsExtensions.CompleteProblemDetails);
orderGeneratorBuilder.Services.AddExceptionHandler<GlobalErrorHandler>();

var orderGeneratorApp = orderGeneratorBuilder.Build();

orderGeneratorApp.UseExceptionHandler();
orderGeneratorApp.UseStatusCodePages();

orderGeneratorApp.UseDefaultFiles();
orderGeneratorApp.UseStaticFiles();
orderGeneratorApp.MapOrderGeneratorRoutes(buildCommitSha);
orderGeneratorApp.MapFallbackToFile("index.html");

orderGeneratorApp.Run();

// Lets the WebApplicationFactory of the tests see the entry point.
public partial class Program;
