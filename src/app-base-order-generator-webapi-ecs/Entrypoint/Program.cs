using OrderGenerator;

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

orderGeneratorBuilder.Services.AddSingleton<FixOrderClient>();
orderGeneratorBuilder.Services.AddHostedService(orderGeneratorServices => orderGeneratorServices.GetRequiredService<FixOrderClient>());

orderGeneratorBuilder.Services.AddHttpClient(OrderGeneratorApiEndpoints.AccumulatorHttpClientName, (orderGeneratorServices, accumulatorClient) =>
{
    var accumulatorBaseUrl = orderGeneratorServices.GetRequiredService<IConfiguration>()["OrderAccumulator:BaseUrl"]
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

// Contrato §4: a porta HTTP vem de ASPNETCORE_HTTP_PORTS; 8080 só quando ninguém informou porta nem URL.
public static class OrderGeneratorHttpPortConfiguration
{
    public const string DefaultOrderGeneratorHttpPort = "8080";

    public static void UseDefaultOrderGeneratorHttpPortWhenMissing(WebApplicationBuilder orderGeneratorBuilder)
    {
        if (string.IsNullOrEmpty(orderGeneratorBuilder.Configuration["HTTP_PORTS"])
            && string.IsNullOrEmpty(orderGeneratorBuilder.Configuration["URLS"]))
            orderGeneratorBuilder.WebHost.UseSetting(WebHostDefaults.HttpPortsKey, DefaultOrderGeneratorHttpPort);
    }
}
