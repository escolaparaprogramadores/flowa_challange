using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Flowa.OrderGenerator.Tests;

// Decision 1 and CA-5: GET /api/exposures reads the exposure straight in the PostgreSQL and answers the body the
// OrderAccumulator answered in ff9fe15, in one DataMessage.
[Collection(OrderGeneratorPostgresCollection.Name)]
public sealed class ExposureDatabaseTests : IAsyncLifetime
{
    private const string ExposuresReadMessage = "Exposição dos símbolos lida.";

    private readonly OrderGeneratorPostgresFixture _orderGeneratorPostgres;

    public ExposureDatabaseTests(OrderGeneratorPostgresFixture orderGeneratorPostgres) => _orderGeneratorPostgres = orderGeneratorPostgres;

    public Task InitializeAsync() => _orderGeneratorPostgres.CreateEmptyOrderTablesAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Reads_the_exposure_of_the_three_symbols_from_the_database_in_one_data_message()
    {
        await _orderGeneratorPostgres.SetSymbolExposureAsync("PETR4", 1000.00m);
        await _orderGeneratorPostgres.SetSymbolExposureAsync("VALE3", -500.00m);
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(
            OrderGeneratorTestHost.FindFreeTcpPort(), _orderGeneratorPostgres.OrderDatabaseConnectionString);
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();

        var exposuresDataMessage = await OrderApiTests.ReadSuccessDataMessageAsync(await orderGeneratorClient.GetAsync("/api/exposures"));

        Assert.Equal(ExposuresReadMessage, exposuresDataMessage.GetProperty("message").GetString());
        Assert.Equal(
            """{"limit":100000000,"exposures":[{"symbol":"PETR4","exposure":1000.00,"remaining":99999000.00},{"symbol":"VALE3","exposure":-500.00,"remaining":99999500.00},{"symbol":"VIIA4","exposure":0,"remaining":100000000}]}""",
            exposuresDataMessage.GetProperty("data").GetRawText());
        Assert.False(exposuresDataMessage.GetProperty("data").TryGetProperty("data", out _));
    }

    // CA-5: the same read before and after the exposure changes in the database; nothing is kept in memory between calls.
    [Fact]
    public async Task Each_read_sees_the_exposure_the_database_has_now()
    {
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(
            OrderGeneratorTestHost.FindFreeTcpPort(), _orderGeneratorPostgres.OrderDatabaseConnectionString);
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();

        var exposuresBefore = (await OrderApiTests.ReadSuccessDataMessageAsync(await orderGeneratorClient.GetAsync("/api/exposures"))).GetProperty("data");
        await _orderGeneratorPostgres.SetSymbolExposureAsync("VIIA4", 2500.50m);
        var exposuresAfter = (await OrderApiTests.ReadSuccessDataMessageAsync(await orderGeneratorClient.GetAsync("/api/exposures"))).GetProperty("data");

        Assert.Equal(0m, exposuresBefore.GetProperty("exposures")[2].GetProperty("exposure").GetDecimal());
        Assert.Equal(("VIIA4", 2500.50m, 99997499.50m), (
            exposuresAfter.GetProperty("exposures")[2].GetProperty("symbol").GetString(),
            exposuresAfter.GetProperty("exposures")[2].GetProperty("exposure").GetDecimal(),
            exposuresAfter.GetProperty("exposures")[2].GetProperty("remaining").GetDecimal()));
    }

    // A caller that asks for HTML (a browser opening the address) gets the same contract when the database is down.
    [Theory]
    [InlineData("application/json")]
    [InlineData("text/html")]
    public async Task Database_down_becomes_500_problem_without_internal_detail(string acceptedMediaType)
    {
        using var stdoutJsonLogCapture = new StdoutJsonLogCapture();
        string unexpectedErrorBody;
        JsonElement unexpectedErrorProblem;
        await using (var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort()))
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
        Assert.DoesNotContain("Npgsql", unexpectedErrorBody);
        Assert.DoesNotContain("   at ", unexpectedErrorBody);
        // CA-6: one Error line with the whole exception, only in the log.
        var unexpectedErrorLine = OrderLogTests.AssertSingleHttpErrorLine(stdoutJsonLogCapture, "Error", "Unexpected application error.",
            "urn:base-investimentos:problem:internal-error", "GET", "/api/exposures", unexpectedErrorProblem.GetProperty("traceId").GetString());
        Assert.StartsWith("Npgsql.NpgsqlException", unexpectedErrorLine.Exception);
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

        // The page has to come only from the test root, even with the screen build in src/flowa.ordergenerator-webapi-ecs/wwwroot.
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
        // That is where the build and the publish serve the page that F5 generates in src/flowa.ordergenerator-webapi-ecs/wwwroot.
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
