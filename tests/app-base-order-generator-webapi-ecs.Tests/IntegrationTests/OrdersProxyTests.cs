using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Base.OrderGenerator.Tests;

// GET and DELETE /api/orders only forward to the OrderAccumulator (CA-21, CA-22, CA-29, CA-33, CA-34, CA-35, CA-42).
public sealed class OrdersProxyTests : IDisposable
{
    private const string OrderAccumulatorUnavailableMessage = "Não foi possível falar com o OrderAccumulator. Tente de novo em instantes.";
    private const string AccumulatorOrdersPageMessage = "Página de ordens lida.";
    private const string OrderTicketTestIndexHtml = "<!doctype html><title>test-order-ticket</title>";
    private const string AccumulatorTraceId = "0af7651916cd43dd8448eb211c80319c";
    private const string InvalidPageProblemJson = $$"""
        {"type":"urn:base-investimentos:problem:invalid-page","title":"Dados inválidos","status":400,"detail":"Página inválida.","instance":"/api/orders","traceId":"{{AccumulatorTraceId}}","success":false,"statusResultado":"InvalidInput","errors":["A página deve ser um número inteiro de 1 a 1000."]}
        """;

    private readonly string _temporaryWebRoot = Directory.CreateTempSubdirectory("flowa-wwwroot-").FullName;

    public OrdersProxyTests() => File.WriteAllText(Path.Combine(_temporaryWebRoot, "index.html"), OrderTicketTestIndexHtml);

    public void Dispose() => Directory.Delete(_temporaryWebRoot, recursive: true);

    [Theory]
    [InlineData(1, new[] { 12, 11, 10, 9, 8, 7, 6, 5, 4, 3 })]
    [InlineData(2, new[] { 2, 1 })]
    public async Task Orders_list_forwards_the_page_and_returns_the_same_body_with_200(int requestedOrdersPage, int[] orderNumbersNewestFirst)
    {
        await using var fakeAccumulator = await StartFakeOrdersAccumulator(async ordersHttpContext =>
        {
            var pageAskedToAccumulator = int.Parse(ordersHttpContext.Request.Query["page"]!);
            ordersHttpContext.Response.ContentType = "application/json";
            await ordersHttpContext.Response.WriteAsync(BuildAccumulatorOrdersPageDataMessageJson(pageAskedToAccumulator, totalOrders: 12,
                pageAskedToAccumulator == 1 ? [12, 11, 10, 9, 8, 7, 6, 5, 4, 3] : [2, 1]));
        });
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort(), fakeAccumulator.FakeAccumulatorUrl);
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();

        var ordersPageDataMessage = await OrderApiTests.ReadSuccessDataMessageAsync(await orderGeneratorClient.GetAsync($"/api/orders?page={requestedOrdersPage}"));

        // CA-4: the "data" of the OrderAccumulator goes on as it came, in one envelope.
        Assert.Equal(AccumulatorOrdersPageMessage, ordersPageDataMessage.GetProperty("message").GetString());
        Assert.Equal(BuildAccumulatorOrdersPageJson(requestedOrdersPage, 12, orderNumbersNewestFirst), ordersPageDataMessage.GetProperty("data").GetRawText());
        var ordersPageResponseJson = ordersPageDataMessage.GetProperty("data");
        Assert.Equal(requestedOrdersPage, ordersPageResponseJson.GetProperty("page").GetInt32());
        Assert.Equal(12, ordersPageResponseJson.GetProperty("total").GetInt32());
        Assert.Equal(orderNumbersNewestFirst.Select(orderNumber => $"order-{orderNumber}"),
            ordersPageResponseJson.GetProperty("orders").EnumerateArray().Select(listedOrder => listedOrder.GetProperty("orderId").GetString()));
        Assert.Equal([$"GET ?page={requestedOrdersPage}"], fakeAccumulator.ReceivedOrdersRequests);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1001")]
    [InlineData("abc")]
    public async Task Invalid_orders_page_returns_the_accumulator_400_as_the_same_problem(string invalidOrdersPage)
    {
        // The fake OrderAccumulator starts before the capture: its own console log is plain text, not the JSON of the app.
        await using var fakeAccumulator = await StartFakeOrdersAccumulator(WriteInvalidPageProblem);
        using var stdoutJsonLogCapture = new StdoutJsonLogCapture();
        JsonElement invalidPageProblem;
        await using (var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort(), fakeAccumulator.FakeAccumulatorUrl))
        {
            using var orderGeneratorClient = orderGeneratorFactory.CreateClient();
            invalidPageProblem = await OrderApiTests.ReadProblemDetailsAsync(
                await orderGeneratorClient.GetAsync($"/api/orders?page={invalidOrdersPage}"), HttpStatusCode.BadRequest);
        }

        // CA-6: the OrderGenerator logs its own 400 once, with its own trace id (not the one of the OrderAccumulator).
        OrderLogTests.AssertSingleHttpErrorLine(stdoutJsonLogCapture, "Warning", "Expected error in request.",
            "urn:base-investimentos:problem:invalid-page", "GET", "/api/orders", invalidPageProblem.GetProperty("traceId").GetString());
        Assert.NotEqual(AccumulatorTraceId, invalidPageProblem.GetProperty("traceId").GetString());
        Assert.Equal("urn:base-investimentos:problem:invalid-page", invalidPageProblem.GetProperty("type").GetString());
        Assert.Equal("Dados inválidos", invalidPageProblem.GetProperty("title").GetString());
        Assert.Equal("Página inválida.", invalidPageProblem.GetProperty("detail").GetString());
        Assert.Equal("InvalidInput", invalidPageProblem.GetProperty("statusResultado").GetString());
        Assert.Equal(["A página deve ser um número inteiro de 1 a 1000."], OrderApiTests.ReadProblemErrorMessages(invalidPageProblem));
        Assert.Equal([$"GET ?page={invalidOrdersPage}"], fakeAccumulator.ReceivedOrdersRequests);
    }

    // The 400 of an OrderAccumulator of the version before (validation_error body, not problem+json) is not the
    // contract: 503 instead of a 500.
    [Fact]
    public async Task Invalid_orders_page_without_problem_json_becomes_503()
    {
        await using var fakeAccumulator = await StartFakeOrdersAccumulator(async ordersHttpContext =>
        {
            ordersHttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            ordersHttpContext.Response.ContentType = "application/json";
            await ordersHttpContext.Response.WriteAsync("""{"status":"validation_error","message":"Página inválida.","errors":[{"field":"page","message":"A página deve ser um número inteiro de 1 a 1000."}]}""");
        });
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort(), fakeAccumulator.FakeAccumulatorUrl);
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();

        await AssertOrdersCommunicationError(await orderGeneratorClient.GetAsync("/api/orders?page=0"));
    }

    [Theory]
    [InlineData("/api/orders?page=2&pageSize=500", "GET ?page=2")]
    [InlineData("/api/orders?pageSize=500&page=3&symbol=PETR4", "GET ?page=3")]
    [InlineData("/api/orders?page=1%262", "GET ?page=1%262")]
    [InlineData("/api/orders?page=1&page=2", "GET ?page=1%2C2")]
    [InlineData("/api/orders", "GET ")]
    public async Task Only_the_orders_page_reaches_the_accumulator_unchanged(string ordersPagePathAskedByClient, string expectedAccumulatorOrdersRequest)
    {
        await using var fakeAccumulator = await StartFakeOrdersAccumulator(AnswerOrdersPageOrDeletion);
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort(), fakeAccumulator.FakeAccumulatorUrl);
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();

        var ordersPageResponse = await orderGeneratorClient.GetAsync(ordersPagePathAskedByClient);

        Assert.Equal(HttpStatusCode.OK, ordersPageResponse.StatusCode);
        Assert.Equal([expectedAccumulatorOrdersRequest], fakeAccumulator.ReceivedOrdersRequests);
    }

    [Fact]
    public async Task Delete_all_orders_forwards_to_the_accumulator_and_returns_204_without_body()
    {
        await using var fakeAccumulator = await StartFakeOrdersAccumulator(ordersHttpContext =>
        {
            ordersHttpContext.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort(), fakeAccumulator.FakeAccumulatorUrl);
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();

        var ordersDeletionResponse = await orderGeneratorClient.DeleteAsync("/api/orders");

        Assert.Equal(HttpStatusCode.NoContent, ordersDeletionResponse.StatusCode);
        Assert.Empty(await ordersDeletionResponse.Content.ReadAsByteArrayAsync());
        Assert.Equal(["DELETE "], fakeAccumulator.ReceivedOrdersRequests);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("DELETE")]
    public async Task Orders_routes_with_the_accumulator_down_return_503_in_portuguese(string ordersHttpMethod)
    {
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort(), $"http://127.0.0.1:{OrderGeneratorTestHost.FindFreeTcpPort()}");
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();

        var apiResponseClock = Stopwatch.StartNew();
        var ordersResponse = await orderGeneratorClient.SendAsync(new HttpRequestMessage(new HttpMethod(ordersHttpMethod), "/api/orders?page=1"));
        apiResponseClock.Stop();

        await AssertOrdersCommunicationError(ordersResponse);
        Assert.True(apiResponseClock.Elapsed < TimeSpan.FromSeconds(5), $"took {apiResponseClock.Elapsed}");
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("DELETE")]
    public async Task Orders_routes_with_the_accumulator_silent_for_5_seconds_return_503(string ordersHttpMethod)
    {
        await using var fakeAccumulator = await StartFakeOrdersAccumulator(async ordersHttpContext =>
            await Task.Delay(TimeSpan.FromSeconds(8), ordersHttpContext.RequestAborted));
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort(), fakeAccumulator.FakeAccumulatorUrl);
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();

        var apiResponseClock = Stopwatch.StartNew();
        var ordersResponse = await orderGeneratorClient.SendAsync(new HttpRequestMessage(new HttpMethod(ordersHttpMethod), "/api/orders?page=1"));
        apiResponseClock.Stop();

        await AssertOrdersCommunicationError(ordersResponse);
        Assert.InRange(apiResponseClock.Elapsed, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(6.5));
    }

    [Theory]
    [InlineData("GET", StatusCodes.Status500InternalServerError, "GET ?page=1")]
    [InlineData("GET", StatusCodes.Status404NotFound, "GET ?page=1")]
    [InlineData("GET", StatusCodes.Status204NoContent, "GET ?page=1")]
    [InlineData("DELETE", StatusCodes.Status500InternalServerError, "DELETE ")]
    [InlineData("DELETE", StatusCodes.Status200OK, "DELETE ")]
    [InlineData("DELETE", StatusCodes.Status400BadRequest, "DELETE ")]
    public async Task Unexpected_accumulator_status_on_orders_routes_becomes_503(string ordersHttpMethod, int unexpectedAccumulatorStatus, string expectedAccumulatorOrdersRequest)
    {
        await using var fakeAccumulator = await StartFakeOrdersAccumulator(async ordersHttpContext =>
        {
            ordersHttpContext.Response.StatusCode = unexpectedAccumulatorStatus;
            if (unexpectedAccumulatorStatus != StatusCodes.Status204NoContent)
                await ordersHttpContext.Response.WriteAsync("accumulator-detail-that-must-not-leak");
        });
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort(), fakeAccumulator.FakeAccumulatorUrl);
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();

        var ordersResponse = await orderGeneratorClient.SendAsync(new HttpRequestMessage(new HttpMethod(ordersHttpMethod), "/api/orders?page=1"));

        await AssertOrdersCommunicationError(ordersResponse);
        Assert.DoesNotContain("accumulator-detail-that-must-not-leak", await ordersResponse.Content.ReadAsStringAsync());
        // The 503 came from the accumulator answer, in a single call: no retry, not even on delete.
        Assert.Equal([expectedAccumulatorOrdersRequest], fakeAccumulator.ReceivedOrdersRequests);
    }

    // CA-29: opening the address, a prefetched link, a form or another verb delete nothing.
    // The real DELETE at the end is the sentinel: it proves the fake accumulator was listening and only it arrived.
    [Fact]
    public async Task Only_the_delete_verb_deletes_orders_and_no_other_verb_falls_back_to_the_index()
    {
        await using var fakeAccumulator = await StartFakeOrdersAccumulator(AnswerOrdersPageOrDeletion);
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort(),
            fakeAccumulator.FakeAccumulatorUrl, _temporaryWebRoot);
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();

        var ordersRequestsThatMustNotDelete = new (HttpRequestMessage OrdersRequest, HttpStatusCode ExpectedStatus)[]
        {
            (new HttpRequestMessage(HttpMethod.Get, "/api/orders"), HttpStatusCode.OK),
            (new HttpRequestMessage(HttpMethod.Post, "/api/orders") { Content = new StringContent("delete everything", Encoding.UTF8, "text/plain") }, HttpStatusCode.BadRequest),
            (new HttpRequestMessage(HttpMethod.Post, "/api/orders") { Content = new FormUrlEncodedContent([new("action", "delete")]) }, HttpStatusCode.BadRequest),
            (new HttpRequestMessage(HttpMethod.Post, "/api/orders"), HttpStatusCode.BadRequest),
            (new HttpRequestMessage(HttpMethod.Put, "/api/orders"), HttpStatusCode.NotFound),
            (new HttpRequestMessage(HttpMethod.Patch, "/api/orders"), HttpStatusCode.NotFound),
            (new HttpRequestMessage(HttpMethod.Head, "/api/orders"), HttpStatusCode.NotFound),
            (new HttpRequestMessage(HttpMethod.Options, "/api/orders"), HttpStatusCode.NotFound),
        };
        foreach (var (ordersRequest, expectedOrdersResponseStatus) in ordersRequestsThatMustNotDelete)
        {
            var ordersRequestDescription = $"{ordersRequest.Method} {ordersRequest.Content?.Headers.ContentType?.MediaType}";
            var ordersResponse = await orderGeneratorClient.SendAsync(ordersRequest);
            Assert.True(expectedOrdersResponseStatus == ordersResponse.StatusCode, $"{ordersRequestDescription}: expected {expectedOrdersResponseStatus}, got {ordersResponse.StatusCode}");
            Assert.DoesNotContain("test-order-ticket", await ordersResponse.Content.ReadAsStringAsync());
        }
        Assert.Equal(["GET "], fakeAccumulator.ReceivedOrdersRequests);

        var sentinelDeletionResponse = await orderGeneratorClient.DeleteAsync("/api/orders");

        Assert.Equal(HttpStatusCode.NoContent, sentinelDeletionResponse.StatusCode);
        Assert.Equal(["GET ", "DELETE "], fakeAccumulator.ReceivedOrdersRequests);
    }

    [Theory]
    [InlineData("GET", HttpStatusCode.OK, "GET ?page=1")]
    [InlineData("DELETE", HttpStatusCode.NoContent, "DELETE ")]
    [InlineData("OPTIONS", HttpStatusCode.NotFound, null)]
    public async Task Orders_routes_do_not_allow_cross_site_cors(string ordersHttpMethod, HttpStatusCode expectedOrdersResponseStatus, string? expectedAccumulatorOrdersRequest)
    {
        await using var fakeAccumulator = await StartFakeOrdersAccumulator(AnswerOrdersPageOrDeletion);
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort(), fakeAccumulator.FakeAccumulatorUrl);
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();
        var crossSiteOrdersRequest = new HttpRequestMessage(new HttpMethod(ordersHttpMethod), "/api/orders?page=1");
        crossSiteOrdersRequest.Headers.Add("Origin", "https://other-site.example");
        crossSiteOrdersRequest.Headers.Add("Access-Control-Request-Method", "DELETE");

        var crossSiteOrdersResponse = await orderGeneratorClient.SendAsync(crossSiteOrdersRequest);

        // The route really answered: without this, a missing route (404 without headers) would also pass.
        Assert.Equal(expectedOrdersResponseStatus, crossSiteOrdersResponse.StatusCode);
        Assert.Equal(expectedAccumulatorOrdersRequest is null ? [] : [expectedAccumulatorOrdersRequest], fakeAccumulator.ReceivedOrdersRequests);
        var crossSiteResponseHeaderNames = crossSiteOrdersResponse.Headers.Select(responseHeader => responseHeader.Key)
            .Concat(crossSiteOrdersResponse.Content.Headers.Select(contentHeader => contentHeader.Key));
        Assert.DoesNotContain(crossSiteResponseHeaderNames, headerName => headerName.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
    }

    private const string RequestReceivedInformationLine =
        "Information Base.OrderGenerator.Entrypoint.Logging.RequestReceivedLoggingMiddleware: Request received.";
    private const string HttpCallCompletedInformationLine = "Information Base.OrderGenerator.Commons.Http.HttpRequestClient: HTTP call completed.";
    private const string StoredOrdersPageReadInformationLine =
        "Information Base.OrderGenerator.Application.Orders.UseCases.ListOrdersUseCase: Stored orders page read.";
    private const string AllStoredOrdersDeletedInformationLine =
        "Information Base.OrderGenerator.Application.Orders.UseCases.DeleteAllOrdersUseCase: All stored orders deleted.";

    // CA-34 with G-9: the list is called on every page change, so each call writes exactly the CA-21 lines
    // (request received, HTTP call completed, use case fact) and no other Information line, in the good answer,
    // the invalid page, the delete and the accumulator being down.
    [Theory]
    [InlineData("GET", "/api/orders?page=1", true, HttpStatusCode.OK,
        new[] { RequestReceivedInformationLine, HttpCallCompletedInformationLine, StoredOrdersPageReadInformationLine })]
    [InlineData("GET", "/api/orders?page=abc", true, HttpStatusCode.BadRequest,
        new[] { RequestReceivedInformationLine, HttpCallCompletedInformationLine })]
    [InlineData("DELETE", "/api/orders", true, HttpStatusCode.NoContent,
        new[] { RequestReceivedInformationLine, HttpCallCompletedInformationLine, AllStoredOrdersDeletedInformationLine })]
    [InlineData("GET", "/api/orders?page=1", false, HttpStatusCode.ServiceUnavailable,
        new[] { RequestReceivedInformationLine })]
    public async Task Orders_routes_write_only_the_request_call_and_use_case_information_lines(
        string ordersHttpMethod, string ordersPath, bool isAccumulatorRunning, HttpStatusCode expectedOrdersResponseStatus, string[] expectedInformationLines)
    {
        await using var fakeAccumulator = await StartFakeOrdersAccumulator(async ordersHttpContext =>
        {
            if (ordersHttpContext.Request.Query["page"] == "abc")
            {
                await WriteInvalidPageProblem(ordersHttpContext);
                return;
            }
            await AnswerOrdersPageOrDeletion(ordersHttpContext);
        });
        var accumulatorBaseUrl = isAccumulatorRunning ? fakeAccumulator.FakeAccumulatorUrl : $"http://127.0.0.1:{OrderGeneratorTestHost.FindFreeTcpPort()}";
        var orderGeneratorLogCaptureProvider = new OrderGeneratorLogCaptureProvider();
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort(), accumulatorBaseUrl)
            .WithWebHostBuilder(orderGeneratorWebHostBuilder =>
                orderGeneratorWebHostBuilder.ConfigureLogging(orderGeneratorLogging => orderGeneratorLogging.AddProvider(orderGeneratorLogCaptureProvider)));
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();
        // Without this the test would pass with all logging off: the Information level of the app has to be on.
        var orderGeneratorLoggerFactory = orderGeneratorFactory.Services.GetRequiredService<ILoggerFactory>();
        Assert.True(orderGeneratorLoggerFactory.CreateLogger("OrderGenerator").IsEnabled(LogLevel.Information));
        // And the capture has to be on in that path: an Information line of the app reaches it.
        orderGeneratorLoggerFactory.CreateLogger("OrderGenerator").LogInformation("capture-probe");
        Assert.Contains("Information OrderGenerator: capture-probe", orderGeneratorLogCaptureProvider.CapturedLogLines);
        orderGeneratorLogCaptureProvider.ForgetCapturedLogLines();

        var ordersResponse = await orderGeneratorClient.SendAsync(new HttpRequestMessage(new HttpMethod(ordersHttpMethod), ordersPath));

        Assert.Equal(expectedOrdersResponseStatus, ordersResponse.StatusCode);
        // No FIX acceptor runs in this test: the FIX session logs its reconnection attempts, which do not come from the call.
        Assert.Equal(expectedInformationLines, orderGeneratorLogCaptureProvider.CapturedLogLines
            .Where(capturedLogLine => capturedLogLine.StartsWith($"{LogLevel.Information} ")
                && !capturedLogLine.StartsWith($"{LogLevel.Information} Base.OrderGenerator.Infrastructure.Fix.FixSessionLog: "))
            .ToArray());
    }

    // Regression of the F4 self-review: a caller that gives up while the OrderAccumulator is still answering is not an
    // error of the app; it must not turn into a 500 with an Error line (the framework handles it, as in c41ed4b).
    [Fact]
    public async Task Caller_that_gives_up_on_the_orders_list_leaves_no_warning_or_error_line()
    {
        await using var fakeAccumulator = await StartFakeOrdersAccumulator(async ordersHttpContext =>
            await Task.Delay(TimeSpan.FromSeconds(4), ordersHttpContext.RequestAborted));
        var orderGeneratorLogCaptureProvider = new OrderGeneratorLogCaptureProvider();
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort(), fakeAccumulator.FakeAccumulatorUrl)
            .WithWebHostBuilder(orderGeneratorWebHostBuilder =>
                orderGeneratorWebHostBuilder.ConfigureLogging(orderGeneratorLogging => orderGeneratorLogging.AddProvider(orderGeneratorLogCaptureProvider)));
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();
        using var callerGivingUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => orderGeneratorClient.GetAsync("/api/orders?page=1", callerGivingUp.Token));
        await Task.Delay(TimeSpan.FromSeconds(1));

        Assert.Contains(RequestReceivedInformationLine, orderGeneratorLogCaptureProvider.CapturedLogLines);
        Assert.DoesNotContain(orderGeneratorLogCaptureProvider.CapturedLogLines, capturedLogLine =>
            (capturedLogLine.StartsWith($"{LogLevel.Warning} ") || capturedLogLine.StartsWith($"{LogLevel.Error} "))
            && !capturedLogLine.StartsWith($"{LogLevel.Warning} Microsoft.AspNetCore.StaticFiles."));
    }

    private static async Task AnswerOrdersPageOrDeletion(HttpContext ordersHttpContext)
    {
        if (HttpMethods.IsDelete(ordersHttpContext.Request.Method))
        {
            ordersHttpContext.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }
        ordersHttpContext.Response.ContentType = "application/json";
        await ordersHttpContext.Response.WriteAsync(BuildAccumulatorOrdersPageDataMessageJson(1, 0, []));
    }

    private static async Task WriteInvalidPageProblem(HttpContext ordersHttpContext)
    {
        ordersHttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        ordersHttpContext.Response.ContentType = "application/problem+json";
        await ordersHttpContext.Response.WriteAsync(InvalidPageProblemJson);
    }

    private static string BuildAccumulatorOrdersPageDataMessageJson(int ordersPage, int totalOrders, int[] orderNumbersNewestFirst) =>
        $$"""{"success":true,"status":"Ok","message":"{{AccumulatorOrdersPageMessage}}","data":{{BuildAccumulatorOrdersPageJson(ordersPage, totalOrders, orderNumbersNewestFirst)}},"errors":[],"errorCode":null}""";

    private static string BuildAccumulatorOrdersPageJson(int ordersPage, int totalOrders, int[] orderNumbersNewestFirst) =>
        $$"""{"page":{{ordersPage}},"pageSize":10,"total":{{totalOrders}},"orders":[{{string.Join(",", orderNumbersNewestFirst.Select(BuildAccumulatorListedOrderJson))}}]}""";

    private static string BuildAccumulatorListedOrderJson(int orderNumber) =>
        $$"""{"receivedAt":"2026-10-04T12:{{orderNumber:00}}:00Z","status":"accepted","symbol":"PETR4","side":"buy","quantity":100,"price":10.5,"orderId":"order-{{orderNumber}}","clOrdId":"cl-{{orderNumber}}"}""";

    private static async Task AssertOrdersCommunicationError(HttpResponseMessage ordersResponse)
    {
        var unavailableProblem = await OrderApiTests.ReadProblemDetailsAsync(ordersResponse, HttpStatusCode.ServiceUnavailable);
        Assert.Equal("urn:base-investimentos:problem:order-accumulator-unavailable", unavailableProblem.GetProperty("type").GetString());
        Assert.Equal("Serviço indisponível", unavailableProblem.GetProperty("title").GetString());
        Assert.Equal(OrderAccumulatorUnavailableMessage, unavailableProblem.GetProperty("detail").GetString());
        Assert.Equal("ServiceUnavailable", unavailableProblem.GetProperty("statusResultado").GetString());
        Assert.Empty(unavailableProblem.GetProperty("errors").EnumerateArray());
    }

    private static async Task<FakeOrdersAccumulator> StartFakeOrdersAccumulator(RequestDelegate accumulatorOrdersHandler)
    {
        var fakeAccumulatorUrl = $"http://127.0.0.1:{OrderGeneratorTestHost.FindFreeTcpPort()}";
        var fakeAccumulatorBuilder = WebApplication.CreateSlimBuilder();
        fakeAccumulatorBuilder.WebHost.UseUrls(fakeAccumulatorUrl);
        var fakeAccumulatorApp = fakeAccumulatorBuilder.Build();
        var receivedOrdersRequests = new ConcurrentQueue<string>();
        fakeAccumulatorApp.MapMethods("/api/orders", [HttpMethods.Get, HttpMethods.Delete], ordersHttpContext =>
        {
            receivedOrdersRequests.Enqueue($"{ordersHttpContext.Request.Method} {ordersHttpContext.Request.QueryString}");
            return accumulatorOrdersHandler(ordersHttpContext);
        });
        await fakeAccumulatorApp.StartAsync();
        return new FakeOrdersAccumulator(fakeAccumulatorApp, fakeAccumulatorUrl, receivedOrdersRequests);
    }

    private sealed record FakeOrdersAccumulator(WebApplication FakeAccumulatorApp, string FakeAccumulatorUrl, ConcurrentQueue<string> ReceivedOrdersRequestQueue) : IAsyncDisposable
    {
        public IReadOnlyList<string> ReceivedOrdersRequests => ReceivedOrdersRequestQueue.ToList();
        public ValueTask DisposeAsync() => FakeAccumulatorApp.DisposeAsync();
    }

    private sealed class OrderGeneratorLogCaptureProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _capturedOrderGeneratorLogLines = new();
        public IReadOnlyList<string> CapturedLogLines => _capturedOrderGeneratorLogLines.ToList();
        public void ForgetCapturedLogLines() => _capturedOrderGeneratorLogLines.Clear();
        public ILogger CreateLogger(string categoryName) => new OrderGeneratorLogCaptureLogger(categoryName, _capturedOrderGeneratorLogLines);
        public void Dispose() { }

        private sealed class OrderGeneratorLogCaptureLogger(string categoryName, ConcurrentQueue<string> capturedOrderGeneratorLogLines) : ILogger
        {
            public IDisposable? BeginScope<TLogScopeState>(TLogScopeState logScopeState) where TLogScopeState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TLogEntryState>(LogLevel logLevel, EventId logEventId, TLogEntryState logEntryState, Exception? loggedException,
                Func<TLogEntryState, Exception?, string> logMessageFormatter) =>
                capturedOrderGeneratorLogLines.Enqueue($"{logLevel} {categoryName}: {logMessageFormatter(logEntryState, loggedException)}");
        }
    }
}
