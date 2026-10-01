using OrderGenerator;

// Raiz na pasta do binário: appsettings.json e wwwroot são achados de qualquer pasta de onde o processo suba.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

builder.Services.AddSingleton<FixOrderClient>();
builder.Services.AddHostedService(services => services.GetRequiredService<FixOrderClient>());

builder.Services.AddHttpClient(ApiEndpoints.AccumulatorClient, (services, client) =>
{
    var baseUrl = services.GetRequiredService<IConfiguration>()["OrderAccumulator:BaseUrl"]
        ?? throw new InvalidOperationException("Configuração OrderAccumulator:BaseUrl ausente.");
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(5);
});

var app = builder.Build();

// Erro não previsto vira o corpo do contrato, sem stack trace para quem chamou.
app.UseExceptionHandler(errors => errors.Run(context => ApiEndpoints.UnexpectedError().ExecuteAsync(context)));

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapApi();
app.MapFallbackToFile("index.html");

app.Run();

// Deixa o WebApplicationFactory dos testes enxergar o ponto de entrada.
public partial class Program;
