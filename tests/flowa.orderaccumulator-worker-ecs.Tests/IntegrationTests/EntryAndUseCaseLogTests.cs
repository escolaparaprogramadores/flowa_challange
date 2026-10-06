using System.Net;

namespace Flowa.OrderAccumulator.Tests;

// CA-21 and CA-28: one Information line where something enters the app (start, /api request, gauge loop), one at the
// end of each use case with the business fact, none for /health, /version or the framework, read from the real stdout.
[Collection(OrderAccumulatorPostgresCollection.Name)]
public sealed class EntryAndUseCaseLogTests(OrderAccumulatorPostgresFixture orderAccumulatorDatabase) : IAsyncLifetime
{
    private const string ProgramCategory = "Program";
    private const string RequestReceivedCategory = "Flowa.OrderAccumulator.Entrypoint.Logging.RequestReceivedLoggingMiddleware";
    private const string GaugeCategory = "Flowa.OrderAccumulator.Entrypoint.BackgroundService.SymbolExposureGaugeBackgroundService";
    private const string FixSessionLogCategory = "Flowa.OrderAccumulator.Infrastructure.Fix.FixSessionLog";

    public Task InitializeAsync() => orderAccumulatorDatabase.ResetOrdersAndExposuresAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Start_writes_one_application_started_line_and_one_gauge_loop_line()
    {
        string? versionCommit = null;
        var appLogLines = await RunAppAndReadAppLogLinesAsync(async orderAccumulatorTestApp =>
        {
            using var versionDocument = System.Text.Json.JsonDocument.Parse(await orderAccumulatorTestApp.CreateClient().GetStringAsync("/version"));
            versionCommit = versionDocument.RootElement.GetProperty("commit").GetString();
        });

        var applicationStartedLine = Assert.Single(appLogLines, appLogLine => appLogLine.Category == ProgramCategory);
        Assert.Equal(("Information", "Application started."), (applicationStartedLine.LogLevel, applicationStartedLine.Message));
        Assert.Matches("^[0-9a-f]{40}$", versionCommit);
        Assert.Equal((versionCommit, "Development"), (applicationStartedLine.ReadLogField("BuildCommitSha"), applicationStartedLine.ReadLogField("Environment")));
        var gaugeLoopLine = Assert.Single(appLogLines, appLogLine => appLogLine.Category == GaugeCategory);
        Assert.Equal(("Information", "Symbol exposure gauge loop started.", "30"), (gaugeLoopLine.LogLevel, gaugeLoopLine.Message, gaugeLoopLine.ReadLogField("IntervalSeconds")));
        Assert.Equal(2, appLogLines.Count);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/version")]
    public async Task Health_and_version_requests_write_no_line(string requestPath)
    {
        var appLogLines = await RunAppAndReadAppLogLinesAsync(async orderAccumulatorTestApp =>
            Assert.Equal(HttpStatusCode.OK, (await orderAccumulatorTestApp.CreateClient().GetAsync(requestPath)).StatusCode));

        Assert.Equal([GaugeCategory, ProgramCategory], appLogLines.Select(appLogLine => appLogLine.Category).Order(StringComparer.Ordinal));
    }

    // Regression of the F4 review finding, applied to the Accumulator: the request line carries the route template, never
    // the text the caller typed, and a path no route answers writes only its 404 Warning.
    [Fact]
    public async Task Unknown_api_path_writes_no_request_line_and_never_copies_the_caller_text()
    {
        var appLogLines = await RunAppAndReadAppLogLinesAsync(async orderAccumulatorTestApp =>
            Assert.Equal(HttpStatusCode.NotFound, (await orderAccumulatorTestApp.CreateClient().GetAsync("/api/injected-caller-text?x=forged")).StatusCode));

        var requestLines = WithoutStartLines(appLogLines);
        var notFoundLine = Assert.Single(requestLines);
        Assert.Equal(
            ("Warning", "Flowa.OrderAccumulator.Entrypoint.ErrorHandling.GlobalErrorHandler", "Expected error in request."),
            (notFoundLine.LogLevel, notFoundLine.Category, notFoundLine.Message));
        Assert.DoesNotContain(appLogLines, appLogLine => appLogLine.Category == RequestReceivedCategory);
    }

    [Fact]
    public async Task Order_list_request_writes_one_request_line_and_one_use_case_line()
    {
        var appLogLines = await RunAppAndReadAppLogLinesAsync(async orderAccumulatorTestApp =>
            Assert.Equal(HttpStatusCode.OK, (await orderAccumulatorTestApp.CreateClient().GetAsync("/api/orders?page=2")).StatusCode));

        var requestLines = WithoutStartLines(appLogLines);
        Assert.Equal(2, requestLines.Count);
        AssertRequestReceivedLine(requestLines[0], "GET", "/api/orders");
        Assert.Equal(
            ("Information", "Flowa.OrderAccumulator.Application.Orders.UseCases.ListOrdersUseCase", "Stored orders page read.", "2", "0", "0"),
            (requestLines[1].LogLevel, requestLines[1].Category, requestLines[1].Message,
                requestLines[1].ReadLogField("PageNumber"), requestLines[1].ReadLogField("TotalStoredOrders"), requestLines[1].ReadLogField("OrdersOnPage")));
        Assert.Equal(requestLines[0].TraceId, requestLines[1].TraceId);
        Assert.Matches("^[0-9a-f]{32}$", requestLines[0].TraceId);
        Assert.Matches("^[0-9a-f]{16}$", requestLines[1].ReadLogField("SpanId"));
    }

    [Fact]
    public async Task Exposures_request_writes_one_request_line_and_one_use_case_line()
    {
        var appLogLines = await RunAppAndReadAppLogLinesAsync(async orderAccumulatorTestApp =>
            Assert.Equal(HttpStatusCode.OK, (await orderAccumulatorTestApp.CreateClient().GetAsync("/api/exposures")).StatusCode));

        var requestLines = WithoutStartLines(appLogLines);
        Assert.Equal(2, requestLines.Count);
        AssertRequestReceivedLine(requestLines[0], "GET", "/api/exposures");
        Assert.Equal(
            ("Information", "Flowa.OrderAccumulator.Application.Exposures.UseCases.GetExposuresUseCase", "Symbol exposures read.", "3"),
            (requestLines[1].LogLevel, requestLines[1].Category, requestLines[1].Message, requestLines[1].ReadLogField("SymbolCount")));
        AssertSameRequestTrace(requestLines);
    }

    [Fact]
    public async Task Delete_all_request_writes_one_request_line_and_one_use_case_line()
    {
        var appLogLines = await RunAppAndReadAppLogLinesAsync(async orderAccumulatorTestApp =>
            Assert.Equal(HttpStatusCode.NoContent, (await orderAccumulatorTestApp.CreateClient().DeleteAsync("/api/orders")).StatusCode));

        var requestLines = WithoutStartLines(appLogLines);
        Assert.Equal(2, requestLines.Count);
        AssertRequestReceivedLine(requestLines[0], "DELETE", "/api/orders");
        Assert.Equal(
            ("Information", "Flowa.OrderAccumulator.Application.Orders.UseCases.DeleteAllOrdersUseCase", "All orders deleted and symbol exposures zeroed."),
            (requestLines[1].LogLevel, requestLines[1].Category, requestLines[1].Message));
        AssertSameRequestTrace(requestLines);
    }

    private static void AssertSameRequestTrace(List<JsonLogLine> requestLines)
    {
        Assert.Matches("^[0-9a-f]{32}$", requestLines[0].TraceId);
        Assert.Equal(requestLines[0].TraceId, requestLines[1].TraceId);
        Assert.All(requestLines, requestLine => Assert.Matches("^[0-9a-f]{16}$", requestLine.ReadLogField("SpanId")));
    }

    private static void AssertRequestReceivedLine(JsonLogLine requestReceivedLine, string expectedMethod, string expectedRoute)
    {
        Assert.Equal(
            ("Information", RequestReceivedCategory, "Request received.", expectedMethod, expectedRoute),
            (requestReceivedLine.LogLevel, requestReceivedLine.Category, requestReceivedLine.Message,
                requestReceivedLine.ReadLogField("Method"), requestReceivedLine.ReadLogField("Route")));
        Assert.Null(requestReceivedLine.ReadLogField("Path"));
    }

    private static List<JsonLogLine> WithoutStartLines(IReadOnlyList<JsonLogLine> appLogLines) =>
        appLogLines.Where(appLogLine => appLogLine.Category is not (ProgramCategory or GaugeCategory)).ToList();

    // The lines the application writes, except the QuickFIX session events, which were already there in c41ed4b and stay in
    // the FixSessionLog. The ASP.NET Core category, in Warning (CA-28), may not write a single Information line.
    private async Task<IReadOnlyList<JsonLogLine>> RunAppAndReadAppLogLinesAsync(Func<OrderAccumulatorFixTestHost, Task> exerciseApp)
    {
        using var stdoutJsonLogCapture = new StdoutJsonLogCapture();
        await using (var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor())
        {
            await exerciseApp(orderAccumulatorTestApp);
        }

        var stdoutLogLines = stdoutJsonLogCapture.JsonLogLines;
        Assert.DoesNotContain(stdoutLogLines, stdoutLogLine =>
            stdoutLogLine.Category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) && stdoutLogLine.LogLevel == "Information");
        return stdoutLogLines
            .Where(stdoutLogLine => stdoutLogLine.Category == ProgramCategory || stdoutLogLine.Category.StartsWith("Flowa.OrderAccumulator.", StringComparison.Ordinal))
            .Where(stdoutLogLine => stdoutLogLine.Category != FixSessionLogCategory)
            .ToList();
    }
}
