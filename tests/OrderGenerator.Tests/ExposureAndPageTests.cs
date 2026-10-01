using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace OrderGenerator.Tests;

// D-10: o OrderGenerator só repassa a exposição do OrderAccumulator.
public sealed class ExposureProxyTests
{
    private const string ExposureMessage = "Não foi possível ler a exposição no OrderAccumulator. Tente de novo em instantes.";

    private const string AccumulatorBody = """
        {"limit":100000000.00,"exposures":[{"symbol":"PETR4","exposure":1000.00,"remaining":99999000.00},{"symbol":"VALE3","exposure":-500.00,"remaining":99999500.00},{"symbol":"VIIA4","exposure":0.00,"remaining":100000000.00}]}
        """;

    [Fact]
    public async Task Repassa_o_corpo_do_accumulator_com_status_200()
    {
        await using var accumulator = await FakeAccumulator(async context =>
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(AccumulatorBody);
        });
        await using var factory = TestHost.Generator(TestHost.FreePort(), accumulator.Url);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/exposures");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(AccumulatorBody, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Accumulator_fora_do_ar_responde_503_em_portugues()
    {
        await using var factory = TestHost.Generator(TestHost.FreePort(), $"http://127.0.0.1:{TestHost.FreePort()}");
        using var client = factory.CreateClient();

        var clock = Stopwatch.StartNew();
        var response = await client.GetAsync("/api/exposures");
        clock.Stop();

        await AssertExposureError(response);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"levou {clock.Elapsed}");
    }

    [Fact]
    public async Task Accumulator_que_nao_responde_em_5_segundos_vira_503()
    {
        await using var accumulator = await FakeAccumulator(async context =>
            await Task.Delay(TimeSpan.FromSeconds(8), context.RequestAborted));
        await using var factory = TestHost.Generator(TestHost.FreePort(), accumulator.Url);
        using var client = factory.CreateClient();

        var clock = Stopwatch.StartNew();
        var response = await client.GetAsync("/api/exposures");
        clock.Stop();

        await AssertExposureError(response);
        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(6.5));
    }

    [Fact]
    public async Task Accumulator_com_erro_500_vira_503()
    {
        await using var accumulator = await FakeAccumulator(context =>
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Task.CompletedTask;
        });
        await using var factory = TestHost.Generator(TestHost.FreePort(), accumulator.Url);
        using var client = factory.CreateClient();

        await AssertExposureError(await client.GetAsync("/api/exposures"));
    }

    [Fact]
    public async Task Erro_nao_previsto_vira_500_com_o_corpo_do_contrato_sem_detalhe_interno()
    {
        await using var factory = TestHost.Generator(TestHost.FreePort()).WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services
                .AddHttpClient(ApiEndpoints.AccumulatorClient)
                .ConfigurePrimaryHttpMessageHandler(() => new ExplodingHandler())));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/exposures");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var json = await OrderApiTests.ReadJson(response);
        Assert.Equal("error", json.GetProperty("status").GetString());
        Assert.Equal("Erro inesperado ao processar a ordem.", json.GetProperty("message").GetString());
        Assert.DoesNotContain(ExplodingHandler.Detail, body);
        Assert.DoesNotContain("   at ", body);
    }

    private sealed class ExplodingHandler : HttpMessageHandler
    {
        public const string Detail = "detalhe-interno-que-nao-pode-vazar";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(Detail);
    }

    private static async Task AssertExposureError(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var json = await OrderApiTests.ReadJson(response);
        Assert.Equal("communication_error", json.GetProperty("status").GetString());
        Assert.Equal(ExposureMessage, json.GetProperty("message").GetString());
    }

    private static async Task<FakeServer> FakeAccumulator(RequestDelegate exposures)
    {
        var url = $"http://127.0.0.1:{TestHost.FreePort()}";
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(url);
        var app = builder.Build();
        app.MapGet("/api/exposures", exposures);
        await app.StartAsync();
        return new FakeServer(app, url);
    }

    private sealed record FakeServer(WebApplication App, string Url) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }
}

// Contrato §5: a página sai de wwwroot, com index.html como padrão; /api nunca cai nela.
public sealed class PageTests : IDisposable
{
    private const string IndexHtml = "<!doctype html><title>boleta-de-teste</title>";
    private readonly string _webRoot = Directory.CreateTempSubdirectory("flowa-wwwroot-").FullName;

    public PageTests() => File.WriteAllText(Path.Combine(_webRoot, "index.html"), IndexHtml);

    [Theory]
    [InlineData("/")]
    [InlineData("/boleta")]
    [InlineData("/painel/exposicao")]
    public async Task Raiz_e_rotas_da_tela_devolvem_o_index_html(string path)
    {
        await using var factory = TestHost.Generator(TestHost.FreePort(), webRoot: _webRoot);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(IndexHtml, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/api")]
    [InlineData("/api/nao-existe")]
    [InlineData("/api/orders/123")]
    public async Task Caminho_de_api_desconhecido_responde_404_e_nao_o_index(string path)
    {
        await using var factory = TestHost.Generator(TestHost.FreePort(), webRoot: _webRoot);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("boleta-de-teste", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Health_responde_Healthy_sem_sessao_FIX()
    {
        await using var factory = TestHost.Generator(TestHost.FreePort(), webRoot: _webRoot);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    public void Dispose() => Directory.Delete(_webRoot, recursive: true);
}
