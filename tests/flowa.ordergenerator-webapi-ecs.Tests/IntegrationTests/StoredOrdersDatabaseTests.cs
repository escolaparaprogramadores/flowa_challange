using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Flowa.OrderGenerator.Tests;

// GET and DELETE /api/orders read and delete straight in the PostgreSQL (decision 1, CA-6, CA-7, CA-42, CA-43),
// with the same body, status and page validation the OrderAccumulator answered in ff9fe15.
[Collection(OrderGeneratorPostgresCollection.Name)]
public sealed class StoredOrdersDatabaseTests : IAsyncLifetime, IDisposable
{
    private const string OrdersPageReadMessage = "Página de ordens lida.";
    private const string OrderTicketTestIndexHtml = "<!doctype html><title>test-order-ticket</title>";
    private static readonly DateTime FirstOrderReceivedAt = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    private readonly OrderGeneratorPostgresFixture _orderGeneratorPostgres;
    private readonly string _temporaryWebRoot = Directory.CreateTempSubdirectory("flowa-wwwroot-").FullName;

    public StoredOrdersDatabaseTests(OrderGeneratorPostgresFixture orderGeneratorPostgres)
    {
        _orderGeneratorPostgres = orderGeneratorPostgres;
        File.WriteAllText(Path.Combine(_temporaryWebRoot, "index.html"), OrderTicketTestIndexHtml);
    }

    public Task InitializeAsync() => _orderGeneratorPostgres.CreateEmptyOrderTablesAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose() => Directory.Delete(_temporaryWebRoot, recursive: true);

    [Theory]
    [InlineData(1, new[] { 12, 11, 10, 9, 8, 7, 6, 5, 4, 3 })]
    [InlineData(2, new[] { 2, 1 })]
    public async Task Orders_list_reads_the_page_from_the_database_newest_first_with_200(int requestedOrdersPage, int[] orderNumbersNewestFirst)
    {
        foreach (var orderNumber in Enumerable.Range(1, 12))
            await _orderGeneratorPostgres.InsertStoredOrderAsync(AcceptedBuyOrder(orderNumber));
        await using var orderGeneratorFactory = CreateOrderGeneratorFactoryOnTheTestDatabase();
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();

        var ordersPageDataMessage = await OrderApiTests.ReadSuccessDataMessageAsync(await orderGeneratorClient.GetAsync($"/api/orders?page={requestedOrdersPage}"));

        Assert.Equal(OrdersPageReadMessage, ordersPageDataMessage.GetProperty("message").GetString());
        Assert.Equal(BuildExpectedOrdersPageJson(requestedOrdersPage, 12, orderNumbersNewestFirst), ordersPageDataMessage.GetProperty("data").GetRawText());
    }

    [Fact]
    public async Task Orders_list_maps_the_stored_status_side_and_reject_reason_as_the_accumulator_does()
    {
        await _orderGeneratorPostgres.InsertStoredOrderAsync(new StoredOrderTestRow(
            "cl-rejected", "order-rejected", "exec-rejected", null, "2", 0m, 0m, false, "Símbolo inválido.", FirstOrderReceivedAt));
        await _orderGeneratorPostgres.InsertStoredOrderAsync(new StoredOrderTestRow(
            "cl-unknown-side", "order-unknown-side", "exec-unknown-side", "VALE3", "X", 5m, 1.25m, false, "Lado inválido.", FirstOrderReceivedAt.AddMinutes(1)));
        await using var orderGeneratorFactory = CreateOrderGeneratorFactoryOnTheTestDatabase();
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();

        var ordersPageDataMessage = await OrderApiTests.ReadSuccessDataMessageAsync(await orderGeneratorClient.GetAsync("/api/orders"));

        Assert.Equal(
            """{"page":1,"pageSize":10,"total":2,"orders":[{"receivedAt":"2026-10-04T12:01:00Z","status":"rejected","symbol":"VALE3","side":null,"quantity":5,"price":1.25,"orderId":"order-unknown-side","clOrdId":"cl-unknown-side","rejectReason":"Lado inválido."},{"receivedAt":"2026-10-04T12:00:00Z","status":"rejected","symbol":null,"side":"sell","quantity":0,"price":0,"orderId":"order-rejected","clOrdId":"cl-rejected","rejectReason":"Símbolo inválido."}]}""",
            ordersPageDataMessage.GetProperty("data").GetRawText());
    }

    // RF-04: orders received in the same instant come newest id first (ORDER BY received_at DESC, id DESC).
    [Fact]
    public async Task Orders_received_at_the_same_instant_come_with_the_newest_id_first()
    {
        await _orderGeneratorPostgres.InsertStoredOrderAsync(AcceptedBuyOrder(1) with { ClOrdId = "cl-stored-first", ReceivedAt = FirstOrderReceivedAt });
        await _orderGeneratorPostgres.InsertStoredOrderAsync(AcceptedBuyOrder(2) with { ClOrdId = "cl-stored-second", ReceivedAt = FirstOrderReceivedAt });
        await _orderGeneratorPostgres.InsertStoredOrderAsync(AcceptedBuyOrder(3) with { ClOrdId = "cl-stored-third", ReceivedAt = FirstOrderReceivedAt });
        await using var orderGeneratorFactory = CreateOrderGeneratorFactoryOnTheTestDatabase();
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();

        var ordersPageDataMessage = await OrderApiTests.ReadSuccessDataMessageAsync(await orderGeneratorClient.GetAsync("/api/orders?page=1"));

        Assert.Equal(["cl-stored-third", "cl-stored-second", "cl-stored-first"],
            ordersPageDataMessage.GetProperty("data").GetProperty("orders").EnumerateArray().Select(listedOrder => listedOrder.GetProperty("clOrdId").GetString()));
    }

    // The default factory points at a database nobody listens to: a 400 here proves the page was refused before any read.
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("1001")]
    [InlineData("1.0")]
    [InlineData(" 1")]
    [InlineData("")]
    [InlineData("abc")]
    public async Task Invalid_orders_page_returns_the_400_problem_without_reading_the_database(string invalidOrdersPage)
    {
        using var stdoutJsonLogCapture = new StdoutJsonLogCapture();
        JsonElement invalidPageProblem;
        await using (var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort()))
        {
            using var orderGeneratorClient = orderGeneratorFactory.CreateClient();
            invalidPageProblem = await OrderApiTests.ReadProblemDetailsAsync(
                await orderGeneratorClient.GetAsync($"/api/orders?page={Uri.EscapeDataString(invalidOrdersPage)}"), HttpStatusCode.BadRequest);
        }

        OrderLogTests.AssertSingleHttpErrorLine(stdoutJsonLogCapture, "Warning", "Expected error in request.",
            "urn:base-investimentos:problem:invalid-page", "GET", "/api/orders", invalidPageProblem.GetProperty("traceId").GetString());
        Assert.Equal("urn:base-investimentos:problem:invalid-page", invalidPageProblem.GetProperty("type").GetString());
        Assert.Equal("Dados inválidos", invalidPageProblem.GetProperty("title").GetString());
        Assert.Equal(400, invalidPageProblem.GetProperty("status").GetInt32());
        Assert.Equal("Página inválida.", invalidPageProblem.GetProperty("detail").GetString());
        Assert.Equal("/api/orders", invalidPageProblem.GetProperty("instance").GetString());
        Assert.False(invalidPageProblem.GetProperty("success").GetBoolean());
        Assert.Equal("InvalidInput", invalidPageProblem.GetProperty("statusResultado").GetString());
        Assert.Equal(["A página deve ser um número inteiro de 1 a 1000."], OrderApiTests.ReadProblemErrorMessages(invalidPageProblem));
    }

    [Theory]
    [InlineData("/api/orders?page=1&page=2")]
    [InlineData("/api/orders?page=1%262")]
    public async Task More_than_one_page_value_is_an_invalid_page(string ordersPagePath)
    {
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort());
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();

        var invalidPageProblem = await OrderApiTests.ReadProblemDetailsAsync(await orderGeneratorClient.GetAsync(ordersPagePath), HttpStatusCode.BadRequest);

        Assert.Equal("urn:base-investimentos:problem:invalid-page", invalidPageProblem.GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("/api/orders", 1)]
    [InlineData("/api/orders?page=2&pageSize=500", 2)]
    [InlineData("/api/orders?pageSize=500&page=1&symbol=VALE3", 1)]
    [InlineData("/api/orders?page=0000000002", 2)]
    public async Task Only_the_page_parameter_chooses_the_orders_page(string ordersPagePath, int expectedOrdersPage)
    {
        foreach (var orderNumber in Enumerable.Range(1, 12))
            await _orderGeneratorPostgres.InsertStoredOrderAsync(AcceptedBuyOrder(orderNumber));
        await using var orderGeneratorFactory = CreateOrderGeneratorFactoryOnTheTestDatabase();
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();

        var ordersPageJson = (await OrderApiTests.ReadSuccessDataMessageAsync(await orderGeneratorClient.GetAsync(ordersPagePath))).GetProperty("data");

        Assert.Equal(expectedOrdersPage, ordersPageJson.GetProperty("page").GetInt32());
        Assert.Equal(10, ordersPageJson.GetProperty("pageSize").GetInt32());
        Assert.Equal(expectedOrdersPage == 1 ? 10 : 2, ordersPageJson.GetProperty("orders").GetArrayLength());
    }

    [Fact]
    public async Task Delete_all_orders_zeroes_the_exposure_deletes_the_orders_and_returns_204_without_body()
    {
        await _orderGeneratorPostgres.InsertStoredOrderAsync(AcceptedBuyOrder(1));
        await _orderGeneratorPostgres.InsertStoredOrderAsync(AcceptedBuyOrder(2));
        await _orderGeneratorPostgres.SetSymbolExposureAsync("PETR4", 2100m);
        await _orderGeneratorPostgres.SetSymbolExposureAsync("VALE3", -350.5m);
        await _orderGeneratorPostgres.SetSymbolExposureAsync("VIIA4", 99_999_999m);
        await using var orderGeneratorFactory = CreateOrderGeneratorFactoryOnTheTestDatabase();
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();

        var ordersDeletionResponse = await orderGeneratorClient.DeleteAsync("/api/orders");

        Assert.Equal(HttpStatusCode.NoContent, ordersDeletionResponse.StatusCode);
        Assert.Empty(await ordersDeletionResponse.Content.ReadAsByteArrayAsync());
        Assert.Equal(0, await _orderGeneratorPostgres.CountStoredOrdersAsync());
        Assert.Equal([0m, 0m, 0m], await _orderGeneratorPostgres.ReadSymbolExposuresInOrderAsync());
    }

    // CA-42: zeroing and deleting are one transaction. When deleting the orders fails, the exposure is not left zeroed.
    [Fact]
    public async Task Failed_deletion_of_the_orders_rolls_back_the_zeroed_exposure_and_returns_500()
    {
        await _orderGeneratorPostgres.InsertStoredOrderAsync(AcceptedBuyOrder(1));
        await _orderGeneratorPostgres.SetSymbolExposureAsync("PETR4", 1050m);
        await using (var orderDatabaseConnection = await _orderGeneratorPostgres.OrderDatabaseDataSource.OpenConnectionAsync())
            await orderDatabaseConnection.ExecuteAsync(
                """
                CREATE FUNCTION refuse_order_deletion() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'order deletion refused by the test'; END $$;
                CREATE TRIGGER refuse_order_deletion BEFORE DELETE ON orders FOR EACH ROW EXECUTE FUNCTION refuse_order_deletion();
                """);
        try
        {
            await using var orderGeneratorFactory = CreateOrderGeneratorFactoryOnTheTestDatabase();
            using var orderGeneratorClient = orderGeneratorFactory.CreateClient();

            var failedDeletionProblem = await OrderApiTests.ReadProblemDetailsAsync(await orderGeneratorClient.DeleteAsync("/api/orders"), HttpStatusCode.InternalServerError);

            Assert.Equal("urn:base-investimentos:problem:internal-error", failedDeletionProblem.GetProperty("type").GetString());
            Assert.Equal(1, await _orderGeneratorPostgres.CountStoredOrdersAsync());
            Assert.Equal([1050m, 0m, 0m], await _orderGeneratorPostgres.ReadSymbolExposuresInOrderAsync());
        }
        finally
        {
            await using var orderDatabaseConnection = await _orderGeneratorPostgres.OrderDatabaseDataSource.OpenConnectionAsync();
            await orderDatabaseConnection.ExecuteAsync("DROP TRIGGER refuse_order_deletion ON orders; DROP FUNCTION refuse_order_deletion();");
        }
    }

    // O-12 / decision 19: the delete runs in READ COMMITTED. An order decided while the delete waits on the exposure row
    // is seen by the delete after it commits: at the end the exposure is zero and no order is left. The test only commits
    // once the zeroing UPDATE is seen waiting on the row lock, so the two transactions really overlap; in REPEATABLE READ
    // that waiting UPDATE would fail with a serialization error and the delete would answer 500.
    [Fact]
    public async Task Delete_waiting_on_an_order_being_decided_deletes_it_too_and_ends_with_zero_exposure()
    {
        await using var orderGeneratorFactory = CreateOrderGeneratorFactoryOnTheTestDatabase();
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();
        await using var decidingOrderConnection = await _orderGeneratorPostgres.OrderDatabaseDataSource.OpenConnectionAsync();
        await using var decidingOrderTransaction = await decidingOrderConnection.BeginTransactionAsync();
        await decidingOrderConnection.ExecuteAsync("UPDATE exposures SET exposure = exposure + 1000 WHERE symbol = 'PETR4'", transaction: decidingOrderTransaction);
        await decidingOrderConnection.ExecuteAsync(
            "INSERT INTO orders (cl_ord_id, order_id, exec_id, symbol, side, quantity, price, accepted) VALUES ('cl-deciding', 'order-deciding', 'exec-deciding', 'PETR4', '1', 100, 10, true)",
            transaction: decidingOrderTransaction);

        var ordersDeletionCall = orderGeneratorClient.DeleteAsync("/api/orders");
        await OrderGeneratorTestHost.WaitUntilTestConditionHolds(() => IsExposureZeroingWaitingOnARowLock().GetAwaiter().GetResult());
        Assert.False(ordersDeletionCall.IsCompleted, "the delete did not wait for the exposure row being decided");
        await decidingOrderTransaction.CommitAsync();
        var ordersDeletionResponse = await ordersDeletionCall;

        Assert.Equal(HttpStatusCode.NoContent, ordersDeletionResponse.StatusCode);
        Assert.Equal(0, await _orderGeneratorPostgres.CountStoredOrdersAsync());
        Assert.Equal([0m, 0m, 0m], await _orderGeneratorPostgres.ReadSymbolExposuresInOrderAsync());
    }

    // CA-43: the first start with an empty database; the OrderAccumulator has not created the tables yet.
    [Fact]
    public async Task Without_the_tables_the_routes_answer_zero_exposure_an_empty_list_and_delete_with_204()
    {
        await _orderGeneratorPostgres.DropOrderTablesAsync();
        await using var orderGeneratorFactory = CreateOrderGeneratorFactoryOnTheTestDatabase();
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();

        var exposuresDataMessage = await OrderApiTests.ReadSuccessDataMessageAsync(await orderGeneratorClient.GetAsync("/api/exposures"));
        var ordersPageDataMessage = await OrderApiTests.ReadSuccessDataMessageAsync(await orderGeneratorClient.GetAsync("/api/orders?page=3"));
        var ordersDeletionResponse = await orderGeneratorClient.DeleteAsync("/api/orders");

        Assert.Equal(
            """{"limit":100000000,"exposures":[{"symbol":"PETR4","exposure":0,"remaining":100000000},{"symbol":"VALE3","exposure":0,"remaining":100000000},{"symbol":"VIIA4","exposure":0,"remaining":100000000}]}""",
            exposuresDataMessage.GetProperty("data").GetRawText());
        Assert.Equal("""{"page":3,"pageSize":10,"total":0,"orders":[]}""", ordersPageDataMessage.GetProperty("data").GetRawText());
        Assert.Equal(HttpStatusCode.NoContent, ordersDeletionResponse.StatusCode);
    }

    // Route is the route template, not the path the caller typed: the path can vary (case), the template cannot.
    [Theory]
    [InlineData("GET", "/api/orders?page=2", "/api/orders")]
    [InlineData("DELETE", "/api/orders", "/api/orders")]
    [InlineData("GET", "/api/exposures", "/api/exposures")]
    [InlineData("GET", "/API/Exposures", "/api/exposures")]
    public async Task Database_down_answers_500_without_internal_detail_and_logs_one_error(string databaseHttpMethod, string databasePath, string expectedRouteTemplate)
    {
        using var stdoutJsonLogCapture = new StdoutJsonLogCapture();
        string databaseDownBody;
        JsonElement databaseDownProblem;
        await using (var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort()))
        {
            using var orderGeneratorClient = orderGeneratorFactory.CreateClient();
            var databaseDownResponse = await orderGeneratorClient.SendAsync(new HttpRequestMessage(new HttpMethod(databaseHttpMethod), databasePath));
            databaseDownBody = await databaseDownResponse.Content.ReadAsStringAsync();
            databaseDownProblem = await OrderApiTests.ReadProblemDetailsAsync(databaseDownResponse, HttpStatusCode.InternalServerError);
        }

        Assert.Equal("urn:base-investimentos:problem:internal-error", databaseDownProblem.GetProperty("type").GetString());
        Assert.Equal("Erro interno", databaseDownProblem.GetProperty("title").GetString());
        Assert.Equal("Aconteceu um erro inesperado. Informe o traceId ao suporte.", databaseDownProblem.GetProperty("detail").GetString());
        Assert.Equal("InternalError", databaseDownProblem.GetProperty("statusResultado").GetString());
        Assert.Empty(databaseDownProblem.GetProperty("errors").EnumerateArray());
        Assert.DoesNotContain("Npgsql", databaseDownBody);
        Assert.DoesNotContain("127.0.0.1", databaseDownBody);
        Assert.DoesNotContain("   at ", databaseDownBody);
        var databaseDownErrorLine = OrderLogTests.AssertSingleHttpErrorLine(stdoutJsonLogCapture, "Error", "Unexpected application error.",
            "urn:base-investimentos:problem:internal-error", databaseHttpMethod, expectedRouteTemplate, databaseDownProblem.GetProperty("traceId").GetString());
        Assert.StartsWith("Npgsql.NpgsqlException", databaseDownErrorLine.Exception);
        Assert.DoesNotContain(OrderGeneratorTestHost.UnreachableOrderDatabasePassword, string.Join('\n', stdoutJsonLogCapture.StdoutLines));
    }

    // CA-29: opening the address, a prefetched link, a form or another verb delete nothing.
    // The real DELETE at the end is the sentinel: it proves the stored order was there to be deleted.
    [Fact]
    public async Task Only_the_delete_verb_deletes_orders_and_no_other_verb_falls_back_to_the_index()
    {
        await _orderGeneratorPostgres.InsertStoredOrderAsync(AcceptedBuyOrder(1));
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort(),
            _orderGeneratorPostgres.OrderDatabaseConnectionString, _temporaryWebRoot);
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
        Assert.Equal(1, await _orderGeneratorPostgres.CountStoredOrdersAsync());

        var sentinelDeletionResponse = await orderGeneratorClient.DeleteAsync("/api/orders");

        Assert.Equal(HttpStatusCode.NoContent, sentinelDeletionResponse.StatusCode);
        Assert.Equal(0, await _orderGeneratorPostgres.CountStoredOrdersAsync());
    }

    [Theory]
    [InlineData("GET", HttpStatusCode.OK, 1)]
    [InlineData("DELETE", HttpStatusCode.NoContent, 0)]
    [InlineData("OPTIONS", HttpStatusCode.NotFound, 1)]
    public async Task Orders_routes_do_not_allow_cross_site_cors(string ordersHttpMethod, HttpStatusCode expectedOrdersResponseStatus, long expectedStoredOrdersAfterTheCall)
    {
        await _orderGeneratorPostgres.InsertStoredOrderAsync(AcceptedBuyOrder(1));
        await using var orderGeneratorFactory = CreateOrderGeneratorFactoryOnTheTestDatabase();
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();
        var crossSiteOrdersRequest = new HttpRequestMessage(new HttpMethod(ordersHttpMethod), "/api/orders?page=1");
        crossSiteOrdersRequest.Headers.Add("Origin", "https://other-site.example");
        crossSiteOrdersRequest.Headers.Add("Access-Control-Request-Method", "DELETE");

        var crossSiteOrdersResponse = await orderGeneratorClient.SendAsync(crossSiteOrdersRequest);

        // The route really answered: without this, a missing route (404 without headers) would also pass.
        Assert.Equal(expectedOrdersResponseStatus, crossSiteOrdersResponse.StatusCode);
        Assert.Equal(expectedStoredOrdersAfterTheCall, await _orderGeneratorPostgres.CountStoredOrdersAsync());
        var crossSiteResponseHeaderNames = crossSiteOrdersResponse.Headers.Select(responseHeader => responseHeader.Key)
            .Concat(crossSiteOrdersResponse.Content.Headers.Select(contentHeader => contentHeader.Key));
        Assert.DoesNotContain(crossSiteResponseHeaderNames, headerName => headerName.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
    }

    private const string OrderGeneratorExceptionHandlerCategory = "Flowa.OrderGenerator.Entrypoint.ErrorHandling.OrderGeneratorExceptionHandler";
    private const string RequestReceivedInformationLine =
        "Information Flowa.OrderGenerator.Entrypoint.Logging.RequestReceivedLoggingMiddleware: Request received.";
    private const string StoredOrdersPageReadInformationLine =
        "Information Flowa.OrderGenerator.Application.Orders.UseCases.ListOrdersUseCase: Stored orders page read.";
    private const string AllStoredOrdersDeletedInformationLine =
        "Information Flowa.OrderGenerator.Application.Orders.UseCases.DeleteAllOrdersUseCase: All stored orders deleted.";

    // CA-34 with G-9: the list is called on every page change, so each call writes exactly the request line and the
    // use case fact, and no other Information line: no HTTP call line any more, and none from the database driver.
    [Theory]
    [InlineData("GET", "/api/orders?page=1", true, HttpStatusCode.OK,
        new[] { RequestReceivedInformationLine, StoredOrdersPageReadInformationLine })]
    [InlineData("GET", "/api/orders?page=abc", true, HttpStatusCode.BadRequest,
        new[] { RequestReceivedInformationLine })]
    [InlineData("DELETE", "/api/orders", true, HttpStatusCode.NoContent,
        new[] { RequestReceivedInformationLine, AllStoredOrdersDeletedInformationLine })]
    [InlineData("GET", "/api/orders?page=1", false, HttpStatusCode.InternalServerError,
        new[] { RequestReceivedInformationLine })]
    public async Task Orders_routes_write_only_the_request_and_use_case_information_lines(
        string ordersHttpMethod, string ordersPath, bool isDatabaseRunning, HttpStatusCode expectedOrdersResponseStatus, string[] expectedInformationLines)
    {
        var orderDatabaseConnectionString = isDatabaseRunning
            ? _orderGeneratorPostgres.OrderDatabaseConnectionString
            : OrderGeneratorTestHost.UnreachableOrderDatabaseConnectionString;
        var orderGeneratorLogCaptureProvider = new OrderGeneratorLogCaptureProvider();
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort(), orderDatabaseConnectionString)
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
                && !capturedLogLine.StartsWith($"{LogLevel.Information} Flowa.OrderGenerator.Infrastructure.Fix.FixSessionLog: "))
            .ToArray());
    }

    // Regression of the F4 self-review of the onda 2: a caller that gives up while the database is still answering is
    // not an error of the app; it must not turn into a 500 with an Error line (the framework handles it, as in c41ed4b).
    [Fact]
    public async Task Caller_that_gives_up_on_the_orders_list_leaves_no_warning_or_error_line()
    {
        var orderGeneratorLogCaptureProvider = new OrderGeneratorLogCaptureProvider();
        await using var orderGeneratorFactory = CreateOrderGeneratorFactoryOnTheTestDatabase()
            .WithWebHostBuilder(orderGeneratorWebHostBuilder =>
                orderGeneratorWebHostBuilder.ConfigureLogging(orderGeneratorLogging => orderGeneratorLogging.AddProvider(orderGeneratorLogCaptureProvider)));
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();
        await using var orderTableLock = await _orderGeneratorPostgres.LockOrderTableAsync();
        using var callerGivingUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => orderGeneratorClient.GetAsync("/api/orders?page=1", callerGivingUp.Token));
        await Task.Delay(TimeSpan.FromSeconds(1));

        Assert.Contains(RequestReceivedInformationLine, orderGeneratorLogCaptureProvider.CapturedLogLines);
        Assert.DoesNotContain(orderGeneratorLogCaptureProvider.CapturedLogLines, capturedLogLine =>
            (capturedLogLine.StartsWith($"{LogLevel.Warning} ") || capturedLogLine.StartsWith($"{LogLevel.Error} "))
            && !capturedLogLine.StartsWith($"{LogLevel.Warning} Microsoft.AspNetCore.StaticFiles."));
    }

    // Regression of the F4 architecture review (F-01): the Information lines never copy what the caller typed in the path
    // or in the page, so a huge URL cannot turn into a huge log line; the request line carries the route template, and an
    // unknown /api path writes no request line (its 404 Warning, with the path in the ASP.NET request scope, is the one of c41ed4b).
    [Fact]
    public async Task Huge_path_and_page_typed_by_the_caller_do_not_reach_the_log_lines()
    {
        var hugePageNumber = new string('0', 6000) + "1";
        var hugeUnknownApiPath = new string('x', 6000);
        using var stdoutJsonLogCapture = new StdoutJsonLogCapture();
        await using (var orderGeneratorFactory = CreateOrderGeneratorFactoryOnTheTestDatabase())
        {
            using var orderGeneratorClient = orderGeneratorFactory.CreateClient();
            Assert.Equal(HttpStatusCode.OK, (await orderGeneratorClient.GetAsync($"/api/orders?page={hugePageNumber}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await orderGeneratorClient.GetAsync($"/api/{hugeUnknownApiPath}")).StatusCode);
        }

        var requestReceivedLines = stdoutJsonLogCapture.JsonLogLines.Where(jsonLogLine => jsonLogLine.Message == "Request received.").ToList();
        Assert.Equal(["/api/orders"], requestReceivedLines.Select(requestReceivedLine => requestReceivedLine.ReadLogField("Route")));
        Assert.Null(Assert.Single(requestReceivedLines).ReadLogField("Path"));
        Assert.DoesNotContain(stdoutJsonLogCapture.StdoutLines, stdoutLine => stdoutLine.Contains(hugePageNumber));
        var linesWithTheHugePath = stdoutJsonLogCapture.JsonLogLines.Where(jsonLogLine => jsonLogLine.ReadLogField("RequestPath")?.Contains(hugeUnknownApiPath) == true).ToList();
        Assert.Equal((OrderGeneratorExceptionHandlerCategory, "Warning", "Expected error in request."),
            (Assert.Single(linesWithTheHugePath).Category, linesWithTheHugePath[0].LogLevel, linesWithTheHugePath[0].Message));
        Assert.Equal(1, stdoutJsonLogCapture.StdoutLines.Count(stdoutLine => stdoutLine.Contains(hugeUnknownApiPath)));
    }

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> CreateOrderGeneratorFactoryOnTheTestDatabase() =>
        OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort(), _orderGeneratorPostgres.OrderDatabaseConnectionString);

    private async Task<bool> IsExposureZeroingWaitingOnARowLock()
    {
        await using var lockObserverConnection = await _orderGeneratorPostgres.OrderDatabaseDataSource.OpenConnectionAsync();
        return await lockObserverConnection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND query LIKE 'UPDATE exposures SET exposure = 0%'") == 1;
    }

    private static StoredOrderTestRow AcceptedBuyOrder(int orderNumber) =>
        new($"cl-{orderNumber}", $"order-{orderNumber}", $"exec-{orderNumber}", "PETR4", "1", 100m, 10.5m, true, null,
            FirstOrderReceivedAt.AddMinutes(orderNumber));

    private static string BuildExpectedOrdersPageJson(int ordersPage, int totalOrders, int[] orderNumbersNewestFirst) =>
        $$"""{"page":{{ordersPage}},"pageSize":10,"total":{{totalOrders}},"orders":[{{string.Join(",", orderNumbersNewestFirst.Select(BuildExpectedListedOrderJson))}}]}""";

    private static string BuildExpectedListedOrderJson(int orderNumber) =>
        $$"""{"receivedAt":"2026-10-04T12:{{orderNumber:00}}:00Z","status":"accepted","symbol":"PETR4","side":"buy","quantity":100,"price":10.5,"orderId":"order-{{orderNumber}}","clOrdId":"cl-{{orderNumber}}","rejectReason":null}""";

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
