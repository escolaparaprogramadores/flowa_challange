using Base.OrderGenerator.Entrypoint;
using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Base.OrderGenerator.Tests;

// D-10: o OrderGenerator só repassa a exposição do OrderAccumulator.
public sealed class ExposureProxyTests
{
    private const string ExposureCommunicationMessage = "Não foi possível ler a exposição no OrderAccumulator. Tente de novo em instantes.";

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
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort(), fakeAccumulator.FakeAccumulatorUrl);
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();

        var exposuresResponse = await orderGeneratorClient.GetAsync("/api/exposures");

        Assert.Equal(HttpStatusCode.OK, exposuresResponse.StatusCode);
        Assert.Equal("application/json", exposuresResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(AccumulatorExposuresJson, await exposuresResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Accumulator_fora_do_ar_responde_503_em_portugues()
    {
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort(), $"http://127.0.0.1:{OrderGeneratorTestHost.FindFreeTcpPort()}");
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();

        var apiResponseClock = Stopwatch.StartNew();
        var exposuresResponse = await orderGeneratorClient.GetAsync("/api/exposures");
        apiResponseClock.Stop();

        await AssertExposureError(exposuresResponse);
        Assert.True(apiResponseClock.Elapsed < TimeSpan.FromSeconds(5), $"levou {apiResponseClock.Elapsed}");
    }

    [Fact]
    public async Task Accumulator_que_nao_responde_em_5_segundos_vira_503()
    {
        await using var fakeAccumulator = await StartFakeAccumulator(async exposuresHttpContext =>
            await Task.Delay(TimeSpan.FromSeconds(8), exposuresHttpContext.RequestAborted));
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort(), fakeAccumulator.FakeAccumulatorUrl);
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();

        var apiResponseClock = Stopwatch.StartNew();
        var exposuresResponse = await orderGeneratorClient.GetAsync("/api/exposures");
        apiResponseClock.Stop();

        await AssertExposureError(exposuresResponse);
        Assert.InRange(apiResponseClock.Elapsed, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(6.5));
    }

    [Fact]
    public async Task Accumulator_com_erro_500_vira_503()
    {
        await using var fakeAccumulator = await StartFakeAccumulator(exposuresHttpContext =>
        {
            exposuresHttpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Task.CompletedTask;
        });
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort(), fakeAccumulator.FakeAccumulatorUrl);
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();

        await AssertExposureError(await orderGeneratorClient.GetAsync("/api/exposures"));
    }

    [Fact]
    public async Task Erro_nao_previsto_vira_500_com_o_corpo_do_contrato_sem_detalhe_interno()
    {
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort()).WithWebHostBuilder(orderGeneratorWebHostBuilder =>
            orderGeneratorWebHostBuilder.ConfigureTestServices(testServices => testServices
                .AddHttpClient(OrderGeneratorApiEndpoints.AccumulatorHttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new ExplodingAccumulatorHandler())));
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();

        var unexpectedErrorHttpResponse = await orderGeneratorClient.GetAsync("/api/exposures");
        var unexpectedErrorBody = await unexpectedErrorHttpResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, unexpectedErrorHttpResponse.StatusCode);
        var unexpectedErrorResponseJson = await OrderApiTests.ReadOrderGeneratorResponseJson(unexpectedErrorHttpResponse);
        Assert.Equal("error", unexpectedErrorResponseJson.GetProperty("status").GetString());
        Assert.Equal("Erro inesperado ao processar a ordem.", unexpectedErrorResponseJson.GetProperty("message").GetString());
        Assert.DoesNotContain(ExplodingAccumulatorHandler.InternalErrorDetail, unexpectedErrorBody);
        Assert.DoesNotContain("   at ", unexpectedErrorBody);
    }

    private sealed class ExplodingAccumulatorHandler : HttpMessageHandler
    {
        public const string InternalErrorDetail = "detalhe-interno-que-nao-pode-vazar";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage accumulatorExposuresRequest, CancellationToken accumulatorRequestCancellation) =>
            throw new InvalidOperationException(InternalErrorDetail);
    }

    private static async Task AssertExposureError(HttpResponseMessage exposuresResponse)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, exposuresResponse.StatusCode);
        var communicationErrorResponse = await OrderApiTests.ReadOrderGeneratorResponseJson(exposuresResponse);
        Assert.Equal("communication_error", communicationErrorResponse.GetProperty("status").GetString());
        Assert.Equal(ExposureCommunicationMessage, communicationErrorResponse.GetProperty("message").GetString());
    }

    private static async Task<FakeAccumulatorServer> StartFakeAccumulator(RequestDelegate exposuresHandler)
    {
        var fakeAccumulatorUrl = $"http://127.0.0.1:{OrderGeneratorTestHost.FindFreeTcpPort()}";
        var fakeAccumulatorBuilder = WebApplication.CreateSlimBuilder();
        fakeAccumulatorBuilder.WebHost.UseUrls(fakeAccumulatorUrl);
        var fakeAccumulatorApp = fakeAccumulatorBuilder.Build();
        fakeAccumulatorApp.MapGet("/api/exposures", exposuresHandler);
        await fakeAccumulatorApp.StartAsync();
        return new FakeAccumulatorServer(fakeAccumulatorApp, fakeAccumulatorUrl);
    }

    private sealed record FakeAccumulatorServer(WebApplication FakeAccumulatorApp, string FakeAccumulatorUrl) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => FakeAccumulatorApp.DisposeAsync();
    }
}

// Contrato §5: a página sai de wwwroot, com index.html como padrão; /api nunca cai nela.
public sealed class OrderGeneratorPageTests : IDisposable
{
    private const string BoletaTestIndexHtml = "<!doctype html><title>boleta-de-teste</title>";
    private readonly string _temporaryWebRoot = Directory.CreateTempSubdirectory("flowa-wwwroot-").FullName;

    public OrderGeneratorPageTests() => File.WriteAllText(Path.Combine(_temporaryWebRoot, "index.html"), BoletaTestIndexHtml);

    [Theory]
    [InlineData("/")]
    [InlineData("/boleta")]
    [InlineData("/painel/exposicao")]
    public async Task Raiz_e_rotas_da_tela_devolvem_o_index_html(string pagePath)
    {
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort(), orderGeneratorWebRoot: _temporaryWebRoot);
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();

        var pageResponse = await orderGeneratorClient.GetAsync(pagePath);

        // A página tem de sair só da raiz do teste, mesmo com o build da tela em src/app-base-order-generator-webapi-ecs/wwwroot.
        // Em Development o provedor vira um composto com o wwwroot do projeto na frente; aqui ele é só a pasta do teste.
        var orderGeneratorHostEnvironment = orderGeneratorFactory.Services.GetRequiredService<IWebHostEnvironment>();
        Assert.Equal(Environments.Production, orderGeneratorHostEnvironment.EnvironmentName);
        var webRootFileProvider = Assert.IsType<PhysicalFileProvider>(orderGeneratorHostEnvironment.WebRootFileProvider);
        Assert.Equal(Path.TrimEndingDirectorySeparator(_temporaryWebRoot), Path.TrimEndingDirectorySeparator(webRootFileProvider.Root));
        Assert.Equal(HttpStatusCode.OK, pageResponse.StatusCode);
        Assert.Equal("text/html", pageResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(BoletaTestIndexHtml, await pageResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Sem_trocar_a_raiz_a_pagina_sai_do_wwwroot_ao_lado_do_binario()
    {
        // É de lá que o build e o publish servem a página que a F5 gera em src/app-base-order-generator-webapi-ecs/wwwroot.
        var binaryWebRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var binaryIndexHtml = Path.Combine(binaryWebRoot, "index.html");
        var indexCreatedByThisTest = !File.Exists(binaryIndexHtml);
        if (indexCreatedByThisTest)
        {
            Directory.CreateDirectory(binaryWebRoot);
            File.WriteAllText(binaryIndexHtml, BoletaTestIndexHtml);
        }

        try
        {
            await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort());
            using var orderGeneratorClient = orderGeneratorFactory.CreateClient();

            var pageResponse = await orderGeneratorClient.GetAsync("/");

            var orderGeneratorHostEnvironment = orderGeneratorFactory.Services.GetRequiredService<IWebHostEnvironment>();
            Assert.Equal(Path.TrimEndingDirectorySeparator(binaryWebRoot), Path.TrimEndingDirectorySeparator(orderGeneratorHostEnvironment.WebRootPath));
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
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort(), orderGeneratorWebRoot: _temporaryWebRoot);
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();

        var unknownApiResponse = await orderGeneratorClient.GetAsync(apiPath);

        Assert.Equal(HttpStatusCode.NotFound, unknownApiResponse.StatusCode);
        Assert.DoesNotContain("boleta-de-teste", await unknownApiResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Health_responde_Healthy_sem_sessao_FIX()
    {
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort(), orderGeneratorWebRoot: _temporaryWebRoot);
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();

        var healthResponse = await orderGeneratorClient.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);
        Assert.Equal("Healthy", await healthResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Version_responde_o_commit_do_HEAD_do_repositorio()
    {
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort(), orderGeneratorWebRoot: _temporaryWebRoot);
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();

        var versionResponse = await orderGeneratorClient.GetAsync("/version");

        Assert.Equal(HttpStatusCode.OK, versionResponse.StatusCode);
        var runningOrderGeneratorCommit = (await OrderApiTests.ReadOrderGeneratorResponseJson(versionResponse)).GetProperty("commit").GetString();
        Assert.Equal(ReadRepositoryGitHeadSha(), runningOrderGeneratorCommit);
    }

    // Oráculo de fora do app: o git do repositório onde os testes foram compilados.
    private static string ReadRepositoryGitHeadSha()
    {
        var gitRevParseProcess = Process.Start(new ProcessStartInfo("git", ["-C", AppContext.BaseDirectory, "rev-parse", "HEAD"])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false
        })!;
        var repositoryHeadSha = gitRevParseProcess.StandardOutput.ReadToEnd().Trim();
        gitRevParseProcess.WaitForExit();
        Assert.Equal(0, gitRevParseProcess.ExitCode);
        Assert.Matches("^[0-9a-f]{40}$", repositoryHeadSha);
        return repositoryHeadSha;
    }

    public void Dispose() => Directory.Delete(_temporaryWebRoot, recursive: true);
}
