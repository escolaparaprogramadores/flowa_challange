using Base.OrderGenerator.Commons;
using Base.OrderGenerator.Infrastructure;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Base.OrderGenerator.Tests;

// D-10: the OrderGenerator only passes on the exposure of the OrderAccumulator.
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
    public async Task Accumulator_down_answers_503_in_portuguese()
    {
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort(), $"http://127.0.0.1:{OrderGeneratorTestHost.FindFreeTcpPort()}");
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();

        var apiResponseClock = Stopwatch.StartNew();
        var exposuresResponse = await orderGeneratorClient.GetAsync("/api/exposures");
        apiResponseClock.Stop();

        await AssertExposureError(exposuresResponse);
        Assert.True(apiResponseClock.Elapsed < TimeSpan.FromSeconds(5), $"took {apiResponseClock.Elapsed}");
    }

    [Fact]
    public async Task Accumulator_that_does_not_answer_in_5_seconds_becomes_503()
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
    public async Task Accumulator_with_error_500_becomes_503()
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
        using var stdoutJsonLogCapture = new StdoutJsonLogCapture();
        string unexpectedErrorBody;
        JsonElement unexpectedErrorProblem;
        await using (var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort()).WithWebHostBuilder(orderGeneratorWebHostBuilder =>
            orderGeneratorWebHostBuilder.ConfigureTestServices(testServices => testServices
                .AddHttpClient<IOrderAccumulatorHttpClient, OrderAccumulatorHttpClient>(OrderAccumulatorHttpClient.OrderAccumulatorHttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new ExplodingAccumulatorHandler()))))
        {
            using var orderGeneratorClient = orderGeneratorFactory.CreateClient();
            var exposuresRequest = new HttpRequestMessage(HttpMethod.Get, "/api/exposures");
            exposuresRequest.Headers.Accept.ParseAdd(acceptedMediaType);

            var unexpectedErrorHttpResponse = await orderGeneratorClient.SendAsync(exposuresRequest);
            unexpectedErrorBody = await unexpectedErrorHttpResponse.Content.ReadAsStringAsync();
            unexpectedErrorProblem = await OrderApiTests.ReadProblemDetailsAsync(unexpectedErrorHttpResponse, HttpStatusCode.InternalServerError);
        }

        Assert.Equal("urn:base-investimentos:problem:internal-error", unexpectedErrorProblem.GetProperty("type").GetString());
        Assert.Equal("Erro interno", unexpectedErrorProblem.GetProperty("title").GetString());
        Assert.Equal("Aconteceu um erro inesperado. Informe o traceId ao suporte.", unexpectedErrorProblem.GetProperty("detail").GetString());
        Assert.Equal("InternalError", unexpectedErrorProblem.GetProperty("statusResultado").GetString());
        Assert.Empty(unexpectedErrorProblem.GetProperty("errors").EnumerateArray());
        Assert.DoesNotContain(ExplodingAccumulatorHandler.InternalErrorDetail, unexpectedErrorBody);
        Assert.DoesNotContain("   at ", unexpectedErrorBody);
        // CA-6: one Error line with the whole exception, only in the log.
        var unexpectedErrorLine = OrderLogTests.AssertSingleHttpErrorLine(stdoutJsonLogCapture, "Error", "Unexpected application error.",
            "urn:base-investimentos:problem:internal-error", "GET", "/api/exposures", unexpectedErrorProblem.GetProperty("traceId").GetString());
        Assert.StartsWith($"System.InvalidOperationException: {ExplodingAccumulatorHandler.InternalErrorDetail}", unexpectedErrorLine.Exception);
    }

    private sealed class ExplodingAccumulatorHandler : HttpMessageHandler
    {
        public const string InternalErrorDetail = "internal-detail-that-must-not-leak";

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
        Assert.Empty(unavailableProblem.GetProperty("errors").EnumerateArray());
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

// Contract §5: the page comes from wwwroot, with index.html as the default; /api never falls into it.
public sealed class OrderGeneratorPageTests : IDisposable
{
    private const string OrderTicketTestIndexHtml = "<!doctype html><title>test-order-ticket</title>";
    private readonly string _temporaryWebRoot = Directory.CreateTempSubdirectory("flowa-wwwroot-").FullName;

    public OrderGeneratorPageTests() => File.WriteAllText(Path.Combine(_temporaryWebRoot, "index.html"), OrderTicketTestIndexHtml);

    [Theory]
    [InlineData("/")]
    [InlineData("/order-ticket")]
    [InlineData("/dashboard/exposure")]
    public async Task Root_and_screen_routes_return_the_index_html(string pagePath)
    {
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort(), orderGeneratorWebRoot: _temporaryWebRoot);
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();

        var pageResponse = await orderGeneratorClient.GetAsync(pagePath);

        // The page has to come only from the test root, even with the screen build in src/app-base-order-generator-webapi-ecs/wwwroot.
        // In Development the provider becomes a composite with the project wwwroot in front; here it is only the test folder.
        var orderGeneratorHostEnvironment = orderGeneratorFactory.Services.GetRequiredService<IWebHostEnvironment>();
        Assert.Equal(Environments.Production, orderGeneratorHostEnvironment.EnvironmentName);
        var webRootFileProvider = Assert.IsType<PhysicalFileProvider>(orderGeneratorHostEnvironment.WebRootFileProvider);
        Assert.Equal(Path.TrimEndingDirectorySeparator(_temporaryWebRoot), Path.TrimEndingDirectorySeparator(webRootFileProvider.Root));
        Assert.Equal(HttpStatusCode.OK, pageResponse.StatusCode);
        Assert.Equal("text/html", pageResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(OrderTicketTestIndexHtml, await pageResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Without_changing_the_root_the_page_comes_from_the_wwwroot_next_to_the_binary()
    {
        // That is where the build and the publish serve the page that F5 generates in src/app-base-order-generator-webapi-ecs/wwwroot.
        var binaryWebRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var binaryIndexHtml = Path.Combine(binaryWebRoot, "index.html");
        var indexCreatedByThisTest = !File.Exists(binaryIndexHtml);
        if (indexCreatedByThisTest)
        {
            Directory.CreateDirectory(binaryWebRoot);
            File.WriteAllText(binaryIndexHtml, OrderTicketTestIndexHtml);
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
    [InlineData("/api/does-not-exist", "application/json")]
    [InlineData("/api/nothing", "text/html")]
    [InlineData("/api/orders/123", "application/json")]
    public async Task Unknown_api_path_answers_404_problem_and_not_the_index(string apiPath, string acceptedMediaType)
    {
        using var stdoutJsonLogCapture = new StdoutJsonLogCapture();
        string unknownApiBody;
        JsonElement notFoundProblem;
        await using (var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort(), orderGeneratorWebRoot: _temporaryWebRoot))
        {
            using var orderGeneratorClient = orderGeneratorFactory.CreateClient();
            var unknownApiRequest = new HttpRequestMessage(HttpMethod.Get, apiPath);
            unknownApiRequest.Headers.Accept.ParseAdd(acceptedMediaType);

            var unknownApiResponse = await orderGeneratorClient.SendAsync(unknownApiRequest);
            unknownApiBody = await unknownApiResponse.Content.ReadAsStringAsync();
            notFoundProblem = await OrderApiTests.ReadProblemDetailsAsync(unknownApiResponse, HttpStatusCode.NotFound);
        }

        Assert.DoesNotContain("test-order-ticket", unknownApiBody);
        Assert.Equal("urn:base-investimentos:problem:not-found", notFoundProblem.GetProperty("type").GetString());
        Assert.Equal("Não encontrado", notFoundProblem.GetProperty("title").GetString());
        Assert.Equal("Não encontrado", notFoundProblem.GetProperty("detail").GetString());
        Assert.Equal("NotFound", notFoundProblem.GetProperty("statusResultado").GetString());
        Assert.Empty(notFoundProblem.GetProperty("errors").EnumerateArray());
        OrderLogTests.AssertSingleHttpErrorLine(stdoutJsonLogCapture, "Warning", "Expected error in request.",
            "urn:base-investimentos:problem:not-found", "GET", "/api/{**unknownApiPath}", notFoundProblem.GetProperty("traceId").GetString());
    }

    [Fact]
    public async Task Health_answers_Healthy_without_a_fix_session()
    {
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort(), orderGeneratorWebRoot: _temporaryWebRoot);
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();

        var healthResponse = await orderGeneratorClient.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);
        Assert.Equal("Healthy", await healthResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Version_answers_the_commit_of_the_repository_head()
    {
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort(), orderGeneratorWebRoot: _temporaryWebRoot);
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();

        var versionResponse = await orderGeneratorClient.GetAsync("/version");

        Assert.Equal(HttpStatusCode.OK, versionResponse.StatusCode);
        var runningOrderGeneratorCommit = (await OrderApiTests.ReadOrderGeneratorResponseJson(versionResponse)).GetProperty("commit").GetString();
        Assert.Equal(ReadRepositoryGitHeadSha(), runningOrderGeneratorCommit);
    }

    // Oracle from outside the app: the git of the repository where the tests were built.
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
