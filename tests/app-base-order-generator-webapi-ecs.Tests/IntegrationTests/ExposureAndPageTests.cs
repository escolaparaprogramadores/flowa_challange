using Base.OrderGenerator.Commons;
using Base.OrderGenerator.Infrastructure;
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
    private const string OrderAccumulatorUnavailableMessage = "Não foi possível falar com o OrderAccumulator. Tente de novo em instantes.";

    private const string AccumulatorExposuresDataJson = """
        {"limit":100000000.00,"exposures":[{"symbol":"PETR4","exposure":1000.00,"remaining":99999000.00},{"symbol":"VALE3","exposure":-500.00,"remaining":99999500.00},{"symbol":"VIIA4","exposure":0.00,"remaining":100000000.00}]}
        """;

    private const string AccumulatorExposuresMessage = "Exposição dos símbolos lida.";

    // CA-4: the OrderGenerator answers the DataMessage with the "data" of the OrderAccumulator as it came, without a
    // second envelope around it.
    [Fact]
    public async Task Passes_on_the_data_of_the_accumulator_in_one_data_message()
    {
        await using var fakeAccumulator = await StartFakeAccumulator(async exposuresHttpContext =>
        {
            exposuresHttpContext.Response.ContentType = "application/json";
            await exposuresHttpContext.Response.WriteAsync(
                $$"""{"success":true,"status":"Ok","message":"{{AccumulatorExposuresMessage}}","data":{{AccumulatorExposuresDataJson}},"errors":[],"errorCode":null}""");
        });
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort(), fakeAccumulator.FakeAccumulatorUrl);
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();

        var exposuresDataMessage = await OrderApiTests.ReadSuccessDataMessageAsync(await orderGeneratorClient.GetAsync("/api/exposures"));

        Assert.Equal(AccumulatorExposuresMessage, exposuresDataMessage.GetProperty("message").GetString());
        Assert.Equal(AccumulatorExposuresDataJson, exposuresDataMessage.GetProperty("data").GetRawText());
        Assert.False(exposuresDataMessage.GetProperty("data").TryGetProperty("data", out _));
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

    // An OrderAccumulator of the version before (body without the envelope, during a rolling deploy) or a page that
    // is not JSON did not answer the route as promised: the same 503, never a 500.
    [Theory]
    [InlineData("application/json", AccumulatorExposuresDataJson)]
    [InlineData("text/html", "<html>Bad Gateway</html>")]
    public async Task Accumulator_200_outside_the_data_message_becomes_503(string accumulatorContentType, string accumulatorBody)
    {
        await using var fakeAccumulator = await StartFakeAccumulator(async exposuresHttpContext =>
        {
            exposuresHttpContext.Response.ContentType = accumulatorContentType;
            await exposuresHttpContext.Response.WriteAsync(accumulatorBody);
        });
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort(), fakeAccumulator.FakeAccumulatorUrl);
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();

        await AssertExposureError(await orderGeneratorClient.GetAsync("/api/exposures"));
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

    // A caller that asks for HTML (a browser opening the address) gets the same contract.
    [Theory]
    [InlineData("application/json")]
    [InlineData("text/html")]
    public async Task Unexpected_error_becomes_500_problem_without_internal_detail(string acceptedMediaType)
    {
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort()).WithWebHostBuilder(orderGeneratorWebHostBuilder =>
            orderGeneratorWebHostBuilder.ConfigureTestServices(testServices => testServices
                .AddHttpClient<IOrderAccumulatorHttpClient, OrderAccumulatorHttpClient>(OrderAccumulatorHttpClient.OrderAccumulatorHttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new ExplodingAccumulatorHandler())));
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();
        var exposuresRequest = new HttpRequestMessage(HttpMethod.Get, "/api/exposures");
        exposuresRequest.Headers.Accept.ParseAdd(acceptedMediaType);

        var unexpectedErrorHttpResponse = await orderGeneratorClient.SendAsync(exposuresRequest);
        var unexpectedErrorBody = await unexpectedErrorHttpResponse.Content.ReadAsStringAsync();

        var unexpectedErrorProblem = await OrderApiTests.ReadProblemDetailsAsync(unexpectedErrorHttpResponse, HttpStatusCode.InternalServerError);
        Assert.Equal("urn:base-investimentos:problem:internal-error", unexpectedErrorProblem.GetProperty("type").GetString());
        Assert.Equal("Erro interno", unexpectedErrorProblem.GetProperty("title").GetString());
        Assert.Equal("Aconteceu um erro inesperado. Informe o traceId ao suporte.", unexpectedErrorProblem.GetProperty("detail").GetString());
        Assert.Equal("InternalError", unexpectedErrorProblem.GetProperty("statusResultado").GetString());
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
        var unavailableProblem = await OrderApiTests.ReadProblemDetailsAsync(exposuresResponse, HttpStatusCode.ServiceUnavailable);
        Assert.Equal("urn:base-investimentos:problem:order-accumulator-unavailable", unavailableProblem.GetProperty("type").GetString());
        Assert.Equal("Serviço indisponível", unavailableProblem.GetProperty("title").GetString());
        Assert.Equal(OrderAccumulatorUnavailableMessage, unavailableProblem.GetProperty("detail").GetString());
        Assert.Equal("ServiceUnavailable", unavailableProblem.GetProperty("statusResultado").GetString());
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
    [InlineData("/api", "application/json")]
    [InlineData("/api/nao-existe", "application/json")]
    [InlineData("/api/nada", "text/html")]
    [InlineData("/api/orders/123", "application/json")]
    public async Task Unknown_api_path_answers_404_problem_and_not_the_index(string apiPath, string acceptedMediaType)
    {
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort(), orderGeneratorWebRoot: _temporaryWebRoot);
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();
        var unknownApiRequest = new HttpRequestMessage(HttpMethod.Get, apiPath);
        unknownApiRequest.Headers.Accept.ParseAdd(acceptedMediaType);

        var unknownApiResponse = await orderGeneratorClient.SendAsync(unknownApiRequest);

        Assert.DoesNotContain("boleta-de-teste", await unknownApiResponse.Content.ReadAsStringAsync());
        var notFoundProblem = await OrderApiTests.ReadProblemDetailsAsync(unknownApiResponse, HttpStatusCode.NotFound);
        Assert.Equal("urn:base-investimentos:problem:not-found", notFoundProblem.GetProperty("type").GetString());
        Assert.Equal("Não encontrado", notFoundProblem.GetProperty("title").GetString());
        Assert.Equal("Não encontrado", notFoundProblem.GetProperty("detail").GetString());
        Assert.Equal("NotFound", notFoundProblem.GetProperty("statusResultado").GetString());
        Assert.Empty(notFoundProblem.GetProperty("errors").EnumerateArray());
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
