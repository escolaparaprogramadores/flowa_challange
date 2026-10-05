using Base.OrderGenerator.Application.Orders.SendOrder;
using Base.OrderGenerator.Commons;
using Base.OrderGenerator.Entrypoint;
using Base.OrderGenerator.Infrastructure;
using Base.OrderGenerator.Infrastructure.Fix;
using Base.OrderGenerator.Infrastructure.Logging;
using Base.OrderGenerator.Infrastructure.Tracing;
using System.Diagnostics;

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

orderGeneratorBuilder.Services.AddHttpClient(OrderGeneratorApiEndpoints.AccumulatorHttpClientName, (orderGeneratorServices, accumulatorClient) =>
{
    var accumulatorBaseUrl = orderGeneratorServices.GetRequiredService<IConfiguration>()[OrderGeneratorConfigurationKeys.OrderAccumulatorBaseUrl]
        ?? throw new InvalidOperationException("Configuração OrderAccumulator:BaseUrl ausente.");
    accumulatorClient.BaseAddress = new Uri(accumulatorBaseUrl);
    accumulatorClient.Timeout = TimeSpan.FromSeconds(5);
});

var orderGeneratorApp = orderGeneratorBuilder.Build();

// Erro não previsto vira o corpo do contrato, sem stack trace para quem chamou.
orderGeneratorApp.UseExceptionHandler(unexpectedErrorPipeline =>
    unexpectedErrorPipeline.Run(failedRequestHttpContext => OrderGeneratorApiEndpoints.BuildOrderGeneratorUnexpectedErrorResponse().ExecuteAsync(failedRequestHttpContext)));

orderGeneratorApp.UseDefaultFiles();
orderGeneratorApp.UseStaticFiles();
orderGeneratorApp.MapOrderGeneratorRoutes(buildCommitSha);
orderGeneratorApp.MapFallbackToFile("index.html");

orderGeneratorApp.Run();

// Deixa o WebApplicationFactory dos testes enxergar o ponto de entrada.
public partial class Program;
