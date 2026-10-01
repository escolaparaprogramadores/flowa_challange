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

    private const string AccumulatorExposuresJson = """
        {"limit":100000000.00,"exposures":[{"symbol":"PETR4","exposure":1000.00,"remaining":99999000.00},{"symbol":"VALE3","exposure":-500.00,"remaining":99999500.00},{"symbol":"VIIA4","exposure":0.00,"remaining":100000000.00}]}
        """;

    [Fact]
    public async Task Repassa_o_corpo_do_accumulator_com_status_200()
    {
        await using var fakeAccumulator = await StartFakeAccumulator(async exposuresHttpContext =>
        {
            exposuresHttpContext.Response.ContentType = "application/json";
            await exposuresHttpContext.Response.WriteAsync(AccumulatorExposuresJson);
        });
        await using var orderGenerator = TestHost.CreateOrderGeneratorFactory(TestHost.FreePort(), fakeAccumulator.Url);
        using var orderGeneratorClient = orderGenerator.CreateClient();

        var exposuresResponse = await orderGeneratorClient.GetAsync("/api/exposures");

        Assert.Equal(HttpStatusCode.OK, exposuresResponse.StatusCode);
        Assert.Equal("application/json", exposuresResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(AccumulatorExposuresJson, await exposuresResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Accumulator_fora_do_ar_responde_503_em_portugues()
    {
        await using var orderGenerator = TestHost.CreateOrderGeneratorFactory(TestHost.FreePort(), $"http://127.0.0.1:{TestHost.FreePort()}");
        using var orderGeneratorClient = orderGenerator.CreateClient();

        var responseClock = Stopwatch.StartNew();
        var exposuresResponse = await orderGeneratorClient.GetAsync("/api/exposures");
        responseClock.Stop();

        await AssertExposureError(exposuresResponse);
        Assert.True(responseClock.Elapsed < TimeSpan.FromSeconds(5), $"levou {responseClock.Elapsed}");
    }

    [Fact]
    public async Task Accumulator_que_nao_responde_em_5_segundos_vira_503()
    {
        await using var fakeAccumulator = await StartFakeAccumulator(async exposuresHttpContext =>
            await Task.Delay(TimeSpan.FromSeconds(8), exposuresHttpContext.RequestAborted));
        await using var orderGenerator = TestHost.CreateOrderGeneratorFactory(TestHost.FreePort(), fakeAccumulator.Url);
        using var orderGeneratorClient = orderGenerator.CreateClient();

        var responseClock = Stopwatch.StartNew();
        var exposuresResponse = await orderGeneratorClient.GetAsync("/api/exposures");
        responseClock.Stop();

        await AssertExposureError(exposuresResponse);
        Assert.InRange(responseClock.Elapsed, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(6.5));
    }

    [Fact]
    public async Task Accumulator_com_erro_500_vira_503()
    {
        await using var fakeAccumulator = await StartFakeAccumulator(exposuresHttpContext =>
        {
            exposuresHttpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Task.CompletedTask;
        });
        await using var orderGenerator = TestHost.CreateOrderGeneratorFactory(TestHost.FreePort(), fakeAccumulator.Url);
        using var orderGeneratorClient = orderGenerator.CreateClient();

        await AssertExposureError(await orderGeneratorClient.GetAsync("/api/exposures"));
    }

    [Fact]
    public async Task Erro_nao_previsto_vira_500_com_o_corpo_do_contrato_sem_detalhe_interno()
    {
        await using var orderGenerator = TestHost.CreateOrderGeneratorFactory(TestHost.FreePort()).WithWebHostBuilder(webHostBuilder =>
            webHostBuilder.ConfigureTestServices(testServices => testServices
                .AddHttpClient(ApiEndpoints.AccumulatorHttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new ExplodingHandler())));
        using var orderGeneratorClient = orderGenerator.CreateClient();

        var errorHttpResponse = await orderGeneratorClient.GetAsync("/api/exposures");
        var errorBody = await errorHttpResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, errorHttpResponse.StatusCode);
        var errorResponse = await OrderApiTests.ReadJson(errorHttpResponse);
        Assert.Equal("error", errorResponse.GetProperty("status").GetString());
        Assert.Equal("Erro inesperado ao processar a ordem.", errorResponse.GetProperty("message").GetString());
        Assert.DoesNotContain(ExplodingHandler.Detail, errorBody);
        Assert.DoesNotContain("   at ", errorBody);
    }

    private sealed class ExplodingHandler : HttpMessageHandler
    {
        public const string Detail = "detalhe-interno-que-nao-pode-vazar";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(Detail);
    }

    private static async Task AssertExposureError(HttpResponseMessage exposuresResponse)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, exposuresResponse.StatusCode);
        var communicationErrorResponse = await OrderApiTests.ReadJson(exposuresResponse);
        Assert.Equal("communication_error", communicationErrorResponse.GetProperty("status").GetString());
        Assert.Equal(ExposureMessage, communicationErrorResponse.GetProperty("message").GetString());
    }

    private static async Task<FakeAccumulatorServer> StartFakeAccumulator(RequestDelegate exposuresHandler)
    {
        var fakeAccumulatorUrl = $"http://127.0.0.1:{TestHost.FreePort()}";
        var fakeAccumulatorBuilder = WebApplication.CreateSlimBuilder();
        fakeAccumulatorBuilder.WebHost.UseUrls(fakeAccumulatorUrl);
        var fakeAccumulatorApp = fakeAccumulatorBuilder.Build();
        fakeAccumulatorApp.MapGet("/api/exposures", exposuresHandler);
        await fakeAccumulatorApp.StartAsync();
        return new FakeAccumulatorServer(fakeAccumulatorApp, fakeAccumulatorUrl);
    }

    private sealed record FakeAccumulatorServer(WebApplication App, string Url) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }
}

// Contrato §5: a página sai de wwwroot, com index.html como padrão; /api nunca cai nela.
public sealed class PageTests : IDisposable
{
    private const string IndexHtml = "<!doctype html><title>boleta-de-teste</title>";
    private readonly string _temporaryWebRoot = Directory.CreateTempSubdirectory("flowa-wwwroot-").FullName;

    public PageTests() => File.WriteAllText(Path.Combine(_temporaryWebRoot, "index.html"), IndexHtml);

    [Theory]
    [InlineData("/")]
    [InlineData("/boleta")]
    [InlineData("/painel/exposicao")]
    public async Task Raiz_e_rotas_da_tela_devolvem_o_index_html(string pagePath)
    {
        await using var orderGenerator = TestHost.CreateOrderGeneratorFactory(TestHost.FreePort(), webRoot: _temporaryWebRoot);
        using var orderGeneratorClient = orderGenerator.CreateClient();

        var pageResponse = await orderGeneratorClient.GetAsync(pagePath);

        Assert.Equal(HttpStatusCode.OK, pageResponse.StatusCode);
        Assert.Equal("text/html", pageResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(IndexHtml, await pageResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Sem_trocar_a_raiz_a_pagina_sai_do_wwwroot_ao_lado_do_binario()
    {
        // É de lá que o build e o publish servem a página que a F5 gera em src/OrderGenerator/wwwroot.
        var binaryWebRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var binaryIndexHtml = Path.Combine(binaryWebRoot, "index.html");
        var indexCreatedByThisTest = !File.Exists(binaryIndexHtml);
        if (indexCreatedByThisTest)
        {
            Directory.CreateDirectory(binaryWebRoot);
            File.WriteAllText(binaryIndexHtml, IndexHtml);
        }

        try
        {
            await using var orderGenerator = TestHost.CreateOrderGeneratorFactory(TestHost.FreePort());
            using var orderGeneratorClient = orderGenerator.CreateClient();

            var pageResponse = await orderGeneratorClient.GetAsync("/");

            var hostEnvironment = orderGenerator.Services.GetRequiredService<IWebHostEnvironment>();
            Assert.Equal(Path.TrimEndingDirectorySeparator(binaryWebRoot), Path.TrimEndingDirectorySeparator(hostEnvironment.WebRootPath));
            Assert.Equal(HttpStatusCode.OK, pageResponse.StatusCode);
            Assert.Equal(File.ReadAllText(binaryIndexHtml), await pageResponse.Content.ReadAsStringAsync());
        }
        finally
        {
            if (indexCreatedByThisTest)
            {
                File.Delete(binaryIndexHtml);
                if (!Directory.EnumerateFileSystemEntries(binaryWebRoot).Any())
                    Directory.Delete(binaryWebRoot);
            }
        }
    }

    [Theory]
    [InlineData("/api")]
    [InlineData("/api/nao-existe")]
    [InlineData("/api/orders/123")]
    public async Task Caminho_de_api_desconhecido_responde_404_e_nao_o_index(string apiPath)
    {
        await using var orderGenerator = TestHost.CreateOrderGeneratorFactory(TestHost.FreePort(), webRoot: _temporaryWebRoot);
        using var orderGeneratorClient = orderGenerator.CreateClient();

        var unknownApiResponse = await orderGeneratorClient.GetAsync(apiPath);

        Assert.Equal(HttpStatusCode.NotFound, unknownApiResponse.StatusCode);
        Assert.DoesNotContain("boleta-de-teste", await unknownApiResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Health_responde_Healthy_sem_sessao_FIX()
    {
        await using var orderGenerator = TestHost.CreateOrderGeneratorFactory(TestHost.FreePort(), webRoot: _temporaryWebRoot);
        using var orderGeneratorClient = orderGenerator.CreateClient();

        var healthResponse = await orderGeneratorClient.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);
        Assert.Equal("Healthy", await healthResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Version_responde_o_commit_do_HEAD_do_repositorio()
    {
        await using var orderGenerator = TestHost.CreateOrderGeneratorFactory(TestHost.FreePort(), webRoot: _temporaryWebRoot);
        using var orderGeneratorClient = orderGenerator.CreateClient();

        var versionResponse = await orderGeneratorClient.GetAsync("/version");

        Assert.Equal(HttpStatusCode.OK, versionResponse.StatusCode);
        var runningCommit = (await OrderApiTests.ReadJson(versionResponse)).GetProperty("commit").GetString();
        Assert.Equal(ReadGitHead(), runningCommit);
    }

    // Oráculo de fora do app: o git do repositório onde os testes foram compilados.
    private static string ReadGitHead()
    {
        var gitRevParse = Process.Start(new ProcessStartInfo("git", ["-C", AppContext.BaseDirectory, "rev-parse", "HEAD"])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false
        })!;
        var gitHead = gitRevParse.StandardOutput.ReadToEnd().Trim();
        gitRevParse.WaitForExit();
        Assert.Equal(0, gitRevParse.ExitCode);
        Assert.Matches("^[0-9a-f]{40}$", gitHead);
        return gitHead;
    }

    public void Dispose() => Directory.Delete(_temporaryWebRoot, recursive: true);
}

// Contrato §4: a porta HTTP vem de ASPNETCORE_HTTP_PORTS; 8080 só quando ninguém informou.
public sealed class ContractHttpPortTests
{
    [Fact]
    public void Porta_http_vem_da_variavel_do_contrato_e_8080_so_sem_ela()
    {
        var originalHttpPorts = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS");
        var originalUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", null);

            Environment.SetEnvironmentVariable("ASPNETCORE_HTTP_PORTS", "18080");
            Assert.Equal("18080", HttpPortsChosenAtStartup());

            Environment.SetEnvironmentVariable("ASPNETCORE_HTTP_PORTS", null);
            Assert.Equal(ContractHttpPort.DefaultHttpPort, HttpPortsChosenAtStartup());

            // Com ASPNETCORE_URLS informada quem decide é ela: o 8080 padrão não pode entrar por cima.
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://127.0.0.1:18081");
            Assert.Null(HttpPortsChosenAtStartup());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_HTTP_PORTS", originalHttpPorts);
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", originalUrls);
        }
    }

    // Mesma raiz do Program: lê o appsettings.json que vai junto do binário.
    private static string? HttpPortsChosenAtStartup()
    {
        var startupBuilder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        ContractHttpPort.UseDefaultWhenNotInformed(startupBuilder);
        return startupBuilder.WebHost.GetSetting(WebHostDefaults.HttpPortsKey);
    }
}
