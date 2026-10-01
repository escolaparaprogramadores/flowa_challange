using OrderGenerator;

// O /version promete o sha completo; sem ele o app não sobe, para o erro aparecer no build e não no aceite.
var buildCommitSha = ApiEndpoints.ReadBuildCommitSha() is { Length: 40 } shaFromBuild
    ? shaFromBuild
    : throw new InvalidOperationException(
        "O build não gravou o commit. Compile dentro do repositório git ou passe -p:SourceRevisionId=<sha completo>.");

// Raiz na pasta do binário: appsettings.json e wwwroot são achados de qualquer pasta de onde o processo suba.
var orderGeneratorBuilder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
ContractHttpPort.UseDefaultWhenNotInformed(orderGeneratorBuilder);

orderGeneratorBuilder.Services.AddSingleton<FixOrderClient>();
orderGeneratorBuilder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<FixOrderClient>());

orderGeneratorBuilder.Services.AddHttpClient(ApiEndpoints.AccumulatorHttpClientName, (serviceProvider, accumulatorClient) =>
{
    var accumulatorBaseUrl = serviceProvider.GetRequiredService<IConfiguration>()["OrderAccumulator:BaseUrl"]
        ?? throw new InvalidOperationException("Configuração OrderAccumulator:BaseUrl ausente.");
    accumulatorClient.BaseAddress = new Uri(accumulatorBaseUrl);
    accumulatorClient.Timeout = TimeSpan.FromSeconds(5);
});

var orderGeneratorApp = orderGeneratorBuilder.Build();

// Erro não previsto vira o corpo do contrato, sem stack trace para quem chamou.
orderGeneratorApp.UseExceptionHandler(errorPipeline =>
    errorPipeline.Run(httpContext => ApiEndpoints.UnexpectedErrorResponse().ExecuteAsync(httpContext)));

orderGeneratorApp.UseDefaultFiles();
orderGeneratorApp.UseStaticFiles();
orderGeneratorApp.MapOrderGeneratorRoutes(buildCommitSha);
orderGeneratorApp.MapFallbackToFile("index.html");

orderGeneratorApp.Run();

// Deixa o WebApplicationFactory dos testes enxergar o ponto de entrada.
public partial class Program;

// Contrato §4: a porta HTTP vem de ASPNETCORE_HTTP_PORTS; 8080 só quando ninguém informou porta nem URL.
public static class ContractHttpPort
{
    public const string DefaultHttpPort = "8080";

    public static void UseDefaultWhenNotInformed(WebApplicationBuilder orderGeneratorBuilder)
    {
        if (string.IsNullOrEmpty(orderGeneratorBuilder.Configuration["HTTP_PORTS"])
            && string.IsNullOrEmpty(orderGeneratorBuilder.Configuration["URLS"]))
            orderGeneratorBuilder.WebHost.UseSetting(WebHostDefaults.HttpPortsKey, DefaultHttpPort);
    }
}
