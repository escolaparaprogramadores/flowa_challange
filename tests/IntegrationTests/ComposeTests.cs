using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Flowa.IntegrationTests;

[Collection(ComposeCollection.CollectionName)]
[Trait("Category", "Integration")]
public sealed class ComposeTests(ComposeFixture composeUnderTest)
{
    [Fact]
    public async Task Both_apps_run_in_separate_containers_with_the_fix_roles_of_the_contract()
    {
        var orderGeneratorContainer = await composeUnderTest.InspectServiceContainerAsync("ordergenerator");
        var orderAccumulatorContainer = await composeUnderTest.InspectServiceContainerAsync("orderaccumulator");

        Assert.Equal("running", orderGeneratorContainer.ContainerStatus);
        Assert.Equal("running", orderAccumulatorContainer.ContainerStatus);
        Assert.Equal("dotnet OrderGenerator.dll", orderGeneratorContainer.ContainerEntrypoint);
        Assert.Equal("dotnet OrderAccumulator.dll", orderAccumulatorContainer.ContainerEntrypoint);

        var generatorFixMessages = FixLog.ParseFixMessages(await composeUnderTest.ReadServiceLogAsync("ordergenerator"));
        var initiatorLogon = generatorFixMessages.First(fixMessage => fixMessage.ReadFixTagValue(35) == "A" && fixMessage.ReadFixTagValue(49) == "ORDERGENERATOR");
        Assert.Equal(initiatorLogon, generatorFixMessages.First(fixMessage => fixMessage.ReadFixTagValue(35) == "A"));
        Assert.Equal("FIX.4.4", initiatorLogon.ReadFixTagValue(8));
        Assert.Equal("ORDERACCUMULATOR", initiatorLogon.ReadFixTagValue(56));
        Assert.Equal("30", initiatorLogon.ReadFixTagValue(108));

        var accumulatorFixMessages = FixLog.ParseFixMessages(await composeUnderTest.ReadServiceLogAsync("orderaccumulator"));
        var acceptorLogon = accumulatorFixMessages.First(fixMessage => fixMessage.ReadFixTagValue(35) == "A" && fixMessage.ReadFixTagValue(49) == "ORDERACCUMULATOR");
        Assert.Equal("FIX.4.4", acceptorLogon.ReadFixTagValue(8));
        Assert.Equal("ORDERGENERATOR", acceptorLogon.ReadFixTagValue(56));
    }

    [Fact]
    public async Task Only_the_page_port_is_open_on_the_host_and_the_apps_do_not_run_as_root()
    {
        var orderGeneratorContainer = await composeUnderTest.InspectServiceContainerAsync("ordergenerator");
        var orderAccumulatorContainer = await composeUnderTest.InspectServiceContainerAsync("orderaccumulator");
        var postgresContainer = await composeUnderTest.InspectServiceContainerAsync("postgres");

        // The FIX acceptor only checks SenderCompID/TargetCompID: port 9876 must not leave the compose network.
        Assert.Equal(new[] { $"8080/tcp->127.0.0.1:{ComposeFixture.OrderGeneratorHostPort}" }, orderGeneratorContainer.PublishedPortBindings);
        Assert.Empty(orderAccumulatorContainer.PublishedPortBindings);
        Assert.Empty(postgresContainer.PublishedPortBindings);

        // 1654 is the "app" user that the .NET aspnet image already ships (APP_UID).
        Assert.Equal("1654", await composeUnderTest.ReadContainerProcessUserIdAsync("ordergenerator"));
        Assert.Equal("1654", await composeUnderTest.ReadContainerProcessUserIdAsync("orderaccumulator"));
    }

    [Fact]
    public async Task Both_apps_only_start_after_postgres_is_healthy()
    {
        using var resolvedComposeConfig = await composeUnderTest.ReadResolvedComposeConfigAsync();
        var composeServices = resolvedComposeConfig.RootElement.GetProperty("services");

        var accumulatorDependsOnPostgres = composeServices.GetProperty("orderaccumulator").GetProperty("depends_on").GetProperty("postgres");
        Assert.Equal("service_healthy", accumulatorDependsOnPostgres.GetProperty("condition").GetString());
        var generatorDependencies = composeServices.GetProperty("ordergenerator").GetProperty("depends_on");
        Assert.Equal("service_healthy", generatorDependencies.GetProperty("postgres").GetProperty("condition").GetString());
        Assert.Equal("service_started", generatorDependencies.GetProperty("orderaccumulator").GetProperty("condition").GetString());

        // CA-32: the OrderGenerator uses the same database user as the OrderAccumulator, with its pool limit outside the secret.
        var generatorEnvironment = composeServices.GetProperty("ordergenerator").GetProperty("environment");
        Assert.Equal(composeServices.GetProperty("orderaccumulator").GetProperty("environment").GetProperty("ConnectionStrings__Flowa").GetString(),
            generatorEnvironment.GetProperty("ConnectionStrings__Flowa").GetString());
        Assert.Equal("10", generatorEnvironment.GetProperty("Database__MaximumPoolSize").GetString());
        Assert.DoesNotContain("Pool", generatorEnvironment.GetProperty("ConnectionStrings__Flowa").GetString());
        Assert.False(generatorEnvironment.TryGetProperty("OrderAccumulator__BaseUrl", out _));

        var postgresHealthcheck = composeServices.GetProperty("postgres").GetProperty("healthcheck").GetProperty("test")
            .EnumerateArray().Select(healthcheckCommandPart => healthcheckCommandPart.GetString()).ToArray();
        Assert.Equal(new[] { "CMD-SHELL", "pg_isready -U flowa -d flowa" }, postgresHealthcheck);
    }

    [Fact]
    public async Task Accepted_order_goes_out_as_35_D_and_comes_back_as_35_8_with_the_same_ClOrdID()
    {
        var (acceptedOrder, acceptedOrderMessage) = await PostOrderReadingTheMessageAsync("PETR4", "buy", 100, 10.50m);

        Assert.Equal("accepted", acceptedOrder.GetProperty("status").GetString());
        Assert.Equal("Ordem aceita.", acceptedOrderMessage);
        var clOrdId = acceptedOrder.GetProperty("clOrdId").GetString()!;
        Assert.Matches("^[0-9a-f]{32}$", clOrdId);

        var newOrderSingle = await FindSingleFixMessageAsync("ordergenerator", "D", clOrdId, senderCompId: "ORDERGENERATOR");
        Assert.Equal("PETR4", newOrderSingle.ReadFixTagValue(55));
        Assert.Equal("1", newOrderSingle.ReadFixTagValue(54));
        Assert.Equal("100", newOrderSingle.ReadFixTagValue(38));
        Assert.Equal("2", newOrderSingle.ReadFixTagValue(40));

        var executionReport = await FindSingleFixMessageAsync("orderaccumulator", "8", clOrdId, senderCompId: "ORDERACCUMULATOR");
        Assert.Equal("0", executionReport.ReadFixTagValue(150));
        Assert.Equal("0", executionReport.ReadFixTagValue(39));
        Assert.Equal(acceptedOrder.GetProperty("orderId").GetString(), executionReport.ReadFixTagValue(37));
    }

    // CA-O4 of wave 3: the Datadog tracer comes in both images, with the default sampling at 1.0.
    [Theory]
    [InlineData("ordergenerator")]
    [InlineData("orderaccumulator")]
    public async Task Image_loads_the_Datadog_tracer_with_default_sampling_1(string serviceName)
    {
        Assert.Equal("1", await composeUnderTest.ReadContainerEnvironmentVariableAsync(serviceName, "CORECLR_ENABLE_PROFILING"));
        Assert.Equal("{846F5F1C-F9AE-4B07-969E-05C26BC060D8}", await composeUnderTest.ReadContainerEnvironmentVariableAsync(serviceName, "CORECLR_PROFILER"));
        Assert.Equal("/opt/datadog/Datadog.Trace.ClrProfiler.Native.so", await composeUnderTest.ReadContainerEnvironmentVariableAsync(serviceName, "CORECLR_PROFILER_PATH"));
        Assert.Equal("/opt/datadog", await composeUnderTest.ReadContainerEnvironmentVariableAsync(serviceName, "DD_DOTNET_TRACER_HOME"));
        Assert.Equal("true", await composeUnderTest.ReadContainerEnvironmentVariableAsync(serviceName, "DD_TRACE_OTEL_ENABLED"));
        Assert.Equal("1.0", await composeUnderTest.ReadContainerEnvironmentVariableAsync(serviceName, "DD_TRACE_SAMPLE_RATE"));
    }

    // CA-7, CA-8 and decision 21 end to end, with the Datadog tracer loaded in both images (no agent): the order
    // number is the trace id of its trace, and in both containers each log line of the order is JSON and carries it
    // as TraceId. The traceparent in tag 5100 goes whole to the log (decision 17 fell).
    [Fact]
    public async Task Order_lines_of_both_containers_are_json_with_the_clordid_as_trace_id()
    {
        var acceptedOrder = await PostOrderAsync("PETR4", "buy", 7, 10.10m);
        var clOrdId = acceptedOrder.GetProperty("clOrdId").GetString()!;
        Assert.Matches("^[0-9a-f]{32}$", clOrdId);

        var generatorOrderLines = await ReadOrderLogLinesAsync("ordergenerator", clOrdId);
        Assert.Equal(2, generatorOrderLines.Count);
        var sentNewOrderSingle = Assert.Single(generatorOrderLines, orderLogLine => orderLogLine.Message == "FIX message sent.");
        Assert.Contains("|35=D|", sentNewOrderSingle.ReadLogField("FixMessage"));
        // The trace flags are the Datadog tracer's (it decides the sampling), so only the trace id is fixed here.
        Assert.Matches($@"\|5100=00-{clOrdId}-[0-9a-f]{{16}}-[0-9a-f]{{2}}\|", sentNewOrderSingle.ReadLogField("FixMessage"));
        var receivedExecutionReport = Assert.Single(generatorOrderLines, orderLogLine => orderLogLine.Message == "FIX message received.");
        Assert.Contains("|35=8|", receivedExecutionReport.ReadLogField("FixMessage"));

        // The accepted order also writes the end of its use case inside the order span (CA-21, G-6): FIX in, accepted, FIX out.
        var accumulatorOrderLines = await ReadOrderLogLinesAsync("orderaccumulator", clOrdId);
        Assert.Equal(3, accumulatorOrderLines.Count);
        var acceptedOrderLine = Assert.Single(accumulatorOrderLines, orderLogLine => orderLogLine.Message == "Order accepted.");
        Assert.Equal(("PETR4", "7"), (acceptedOrderLine.ReadLogField("Symbol"), acceptedOrderLine.ReadLogField("Quantity")));
        var receivedNewOrderSingle = Assert.Single(accumulatorOrderLines, orderLogLine => orderLogLine.Message == "FIX message received.");
        Assert.Contains("|35=D|", receivedNewOrderSingle.ReadLogField("FixMessage"));
        var sentExecutionReport = Assert.Single(accumulatorOrderLines, orderLogLine => orderLogLine.Message == "FIX message sent.");
        Assert.Contains("|35=8|", sentExecutionReport.ReadLogField("FixMessage"));
    }

    // CA-7 and decision 22: every stdout line of each container is JSON (the parse fails otherwise), the FIX logon is
    // one of them, and no line is Debug. The heartbeat is proven in FixAcceptorTests, where one is sure to happen.
    [Theory]
    [InlineData("ordergenerator")]
    [InlineData("orderaccumulator")]
    public async Task Container_log_is_json_with_the_fix_logon_and_without_debug(string serviceName)
    {
        var serviceLogLines = await ReadServiceJsonLogLinesAsync(serviceName);

        Assert.Contains(serviceLogLines, serviceLogLine => serviceLogLine.ReadLogField("FixMessage")?.Contains("|35=A|") == true);
        Assert.DoesNotContain(serviceLogLines, serviceLogLine => serviceLogLine.LogLevel is "Debug" or "Trace");
    }

    // RN-01 with the real Datadog tracer: the OrderGenerator does not continue a caller traceparent, so two orders
    // sent in the same caller trace get different ClOrdIDs, and the second is decided, not answered as a repeat.
    [Fact]
    public async Task Orders_sent_with_the_same_caller_traceparent_get_different_clordids()
    {
        const string callerTraceId = "0af7651916cd43dd8448eb211c80319c";
        const string callerTraceParent = $"00-{callerTraceId}-b7ad6b7169203331-01";

        var firstOrder = await PostOrderWithCallerTraceParentAsync(callerTraceParent);
        var secondOrder = await PostOrderWithCallerTraceParentAsync(callerTraceParent);

        var firstClOrdId = firstOrder.GetProperty("clOrdId").GetString();
        var secondClOrdId = secondOrder.GetProperty("clOrdId").GetString();
        Assert.NotEqual(firstClOrdId, secondClOrdId);
        Assert.NotEqual(callerTraceId, firstClOrdId);
        Assert.NotEqual(callerTraceId, secondClOrdId);
        Assert.NotEqual(firstOrder.GetProperty("orderId").GetString(), secondOrder.GetProperty("orderId").GetString());
    }

    [Fact]
    public async Task Order_that_passes_the_limit_comes_back_rejected_with_150_8_and_the_contract_text()
    {
        // Each sale is worth 99,998,000.01; the second would pass 100 million on the same symbol.
        var firstVIIA4Sale = await PostOrderAsync("VIIA4", "sell", 99999, 999.99m);
        Assert.Equal("accepted", firstVIIA4Sale.GetProperty("status").GetString());

        var (secondVIIA4Sale, secondVIIA4SaleMessage) = await PostOrderReadingTheMessageAsync("VIIA4", "sell", 99999, 999.99m);
        const string expectedLimitRejectionText = "Ordem rejeitada: a exposição de VIIA4 passaria do limite de 100.000.000,00.";
        Assert.Equal("rejected", secondVIIA4Sale.GetProperty("status").GetString());
        Assert.Equal(expectedLimitRejectionText, secondVIIA4SaleMessage);

        var clOrdId = secondVIIA4Sale.GetProperty("clOrdId").GetString()!;
        var executionReport = await FindSingleFixMessageAsync("orderaccumulator", "8", clOrdId, senderCompId: "ORDERACCUMULATOR");
        Assert.Equal("8", executionReport.ReadFixTagValue(150));
        Assert.Equal("8", executionReport.ReadFixTagValue(39));
        Assert.Equal(expectedLimitRejectionText, executionReport.ReadFixTagValue(58));
        // CA-13: one Warning in the OrderAccumulator, with the ClOrdID as trace id.
        var limitRejectionWarning = Assert.Single(await ReadOrderLogLinesAsync("orderaccumulator", clOrdId), orderLogLine => orderLogLine.LogLevel == "Warning");
        Assert.Equal("Order rejected: exposure limit exceeded.", limitRejectionWarning.Message);
        Assert.Equal("exposure_limit_exceeded", limitRejectionWarning.ReadLogField("ErrorCode"));
    }

    // CA-28: the field rule lives only in the OrderAccumulator, so an invalid field goes by FIX and comes back rejected.
    [Fact]
    public async Task Order_with_invalid_fields_goes_by_fix_and_comes_back_rejected_with_the_reasons()
    {
        var (invalidOrder, invalidOrderMessage) = await PostOrderReadingTheMessageAsync("ITUB4", "buy", 100000, 1000m);

        const string expectedFieldRejectionText =
            "Símbolo inválido. Use PETR4, VALE3 ou VIIA4. A quantidade deve ser menor que 100.000. O preço deve ser menor que 1.000,00.";
        Assert.Equal("rejected", invalidOrder.GetProperty("status").GetString());
        Assert.Equal(expectedFieldRejectionText, invalidOrderMessage);

        var clOrdId = invalidOrder.GetProperty("clOrdId").GetString()!;
        var newOrderSingle = await FindSingleFixMessageAsync("ordergenerator", "D", clOrdId, senderCompId: "ORDERGENERATOR");
        Assert.Equal("ITUB4", newOrderSingle.ReadFixTagValue(55));
        var executionReport = await FindSingleFixMessageAsync("orderaccumulator", "8", clOrdId, senderCompId: "ORDERACCUMULATOR");
        Assert.Equal("8", executionReport.ReadFixTagValue(150));
        Assert.Equal("8", executionReport.ReadFixTagValue(39));
        Assert.Equal(expectedFieldRejectionText, executionReport.ReadFixTagValue(58));
        // CA-13: one Warning in the OrderAccumulator, with the ClOrdID as trace id.
        var fieldRejectionWarning = Assert.Single(await ReadOrderLogLinesAsync("orderaccumulator", clOrdId), orderLogLine => orderLogLine.LogLevel == "Warning");
        Assert.Equal("Order rejected: invalid fields.", fieldRejectionWarning.Message);
        Assert.Equal("invalid_order_fields", fieldRejectionWarning.ReadLogField("ErrorCode"));
    }

    // Decision 24: what a NewOrderSingle cannot carry stops at the OrderGenerator with a 400 and the reason.
    [Theory]
    [InlineData("""{"symbol":"PETR4","side":"buy","quantity":100}""", "Informe o preço.")]
    [InlineData("""{"symbol":"PETR4","side":"buy","quantity":"abc","price":10.50}""", "A quantidade deve ser um número inteiro.")]
    [InlineData("""{"symbol":"PETR4","side":"compra","quantity":100,"price":10.50}""", "Lado inválido. Use compra ou venda.")]
    public async Task Order_that_does_not_fit_fix_gets_400_problem_with_the_reason(string orderJson, string expectedOrderFieldMessage)
    {
        using var badFormatResponse = await composeUnderTest.OrderGeneratorHttp.PostAsync(
            "/api/orders", new StringContent(orderJson, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, badFormatResponse.StatusCode);
        Assert.Equal("application/problem+json", badFormatResponse.Content.Headers.ContentType?.MediaType);
        var invalidOrderProblem = await badFormatResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("urn:base-investimentos:problem:invalid-order", invalidOrderProblem.GetProperty("type").GetString());
        Assert.Equal("A ordem tem campos inválidos.", invalidOrderProblem.GetProperty("detail").GetString());
        Assert.Matches("^[0-9a-f]{32}$", invalidOrderProblem.GetProperty("traceId").GetString());
        Assert.Equal([expectedOrderFieldMessage], invalidOrderProblem.GetProperty("errors").EnumerateArray().Select(orderFieldMessage => orderFieldMessage.GetString()));
    }

    [Fact]
    public async Task Page_and_exposures_answer_through_the_OrderGenerator()
    {
        using var pageResponse = await composeUnderTest.OrderGeneratorHttp.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, pageResponse.StatusCode);
        Assert.Equal("text/html", pageResponse.Content.Headers.ContentType?.MediaType);
        var pageHtml = await pageResponse.Content.ReadAsStringAsync();
        Assert.Contains("<title>Base investimentos — Boleta de ordens</title>", pageHtml);
        Assert.Contains("<div id=\"raiz\"></div>", pageHtml);

        using var exposuresResponse = await composeUnderTest.OrderGeneratorHttp.GetAsync("/api/exposures");
        Assert.Equal(HttpStatusCode.OK, exposuresResponse.StatusCode);
        var exposuresBody = ReadSuccessData(await exposuresResponse.Content.ReadFromJsonAsync<JsonElement>());
        Assert.Equal(100000000.00m, exposuresBody.GetProperty("limit").GetDecimal());
        var exposureSymbols = exposuresBody.GetProperty("exposures").EnumerateArray()
            .Select(symbolExposureRow => symbolExposureRow.GetProperty("symbol").GetString()!)
            .ToArray();
        Assert.Equal(new[] { "PETR4", "VALE3", "VIIA4" }, exposureSymbols);
    }

    [Fact]
    public async Task After_the_OrderAccumulator_is_recreated_the_OrderGenerator_logs_on_again_by_itself_and_accepts_the_next_order()
    {
        var orderGeneratorContainerBeforeRecreate = await composeUnderTest.InspectServiceContainerAsync("ordergenerator");
        var orderAccumulatorContainerBeforeRecreate = await composeUnderTest.InspectServiceContainerAsync("orderaccumulator");
        var logonsBeforeRecreate = await composeUnderTest.CountFixLogonsFromAcceptorAsync();

        // Only this test uses VALE3: its exposure starts from zero in the test compose.
        var orderBeforeRecreate = await PostOrderAsync("VALE3", "buy", 1, 1.00m);
        Assert.Equal("accepted", orderBeforeRecreate.GetProperty("status").GetString());
        Assert.Equal(1.00m, await ReadSymbolExposureAsync("VALE3"));

        await composeUnderTest.RunComposeCommandAsync(TimeSpan.FromMinutes(3), "up", "-d", "--no-deps", "--force-recreate", "--wait", "orderaccumulator");

        var orderAccumulatorContainerAfterRecreate = await composeUnderTest.InspectServiceContainerAsync("orderaccumulator");
        Assert.NotEqual(orderAccumulatorContainerBeforeRecreate.ContainerId, orderAccumulatorContainerAfterRecreate.ContainerId);

        await composeUnderTest.WaitForNewFixLogonAsync(logonsBeforeRecreate);
        // The recreate is destructive for the tests that run after this one: they only start once the new container
        // logged the logon and answers HTTP. The accepted order at the end proves the FIX session is up when it ends.
        await composeUnderTest.WaitForRecreatedOrderAccumulatorReadyAsync();

        var orderGeneratorContainerAfterRecreate = await composeUnderTest.InspectServiceContainerAsync("ordergenerator");
        Assert.Equal(orderGeneratorContainerBeforeRecreate.ContainerId, orderGeneratorContainerAfterRecreate.ContainerId);
        Assert.Equal(orderGeneratorContainerBeforeRecreate.ContainerStartedAt, orderGeneratorContainerAfterRecreate.ContainerStartedAt);

        // The new OrderAccumulator keeps nothing in memory: it reads the previous exposure from the database.
        Assert.Equal(1.00m, await ReadSymbolExposureAsync("VALE3"));

        var orderAfterRelogon = await PostOrderAsync("VALE3", "buy", 10, 50.00m);
        Assert.Equal("accepted", orderAfterRelogon.GetProperty("status").GetString());
        var clOrdId = orderAfterRelogon.GetProperty("clOrdId").GetString()!;
        var executionReport = await FindSingleFixMessageAsync("orderaccumulator", "8", clOrdId, senderCompId: "ORDERACCUMULATOR");
        Assert.Equal("0", executionReport.ReadFixTagValue(150));
        Assert.Equal(501.00m, await ReadSymbolExposureAsync("VALE3"));
    }

    [Fact]
    public async Task Postgres_data_lives_in_a_named_volume()
    {
        var postgresContainer = await composeUnderTest.InspectServiceContainerAsync("postgres");

        Assert.Equal(new[] { $"volume:{ComposeFixture.ComposeProjectName}_pgdata->/var/lib/postgresql/data" }, postgresContainer.VolumeMounts);
    }

    // RF-10/CA-11 with the real Datadog tracer of the image, not a test listener: with the OrderAccumulator paused the
    // order goes out by FIX and gets no ExecutionReport. The 503 answers the ClOrdID (tag 11 of the NewOrderSingle the
    // OrderGenerator sent) as traceId, and the one log line of the error carries that same id, in its TraceId and in
    // the dd_trace_id the tracer injects.
    [Fact]
    public async Task Order_without_answer_in_5_seconds_answers_and_logs_its_clordid_as_trace_id_with_the_real_tracer()
    {
        JsonElement unansweredOrderProblem;
        await composeUnderTest.RunComposeCommandAsync(TimeSpan.FromMinutes(1), "pause", "orderaccumulator");
        try
        {
            using var unansweredOrderResponse = await composeUnderTest.OrderGeneratorHttp.PostAsJsonAsync(
                "/api/orders", new { symbol = "PETR4", side = "buy", quantity = 1, price = 0.01m });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unansweredOrderResponse.StatusCode);
            Assert.Equal("application/problem+json", unansweredOrderResponse.Content.Headers.ContentType?.MediaType);
            unansweredOrderProblem = await unansweredOrderResponse.Content.ReadFromJsonAsync<JsonElement>();
        }
        finally
        {
            await composeUnderTest.RunComposeCommandAsync(TimeSpan.FromMinutes(1), "unpause", "orderaccumulator");
        }

        const string expectedProblemType = "urn:base-investimentos:problem:execution-report-timeout";
        Assert.Equal(expectedProblemType, unansweredOrderProblem.GetProperty("type").GetString());
        var unansweredOrderTraceId = unansweredOrderProblem.GetProperty("traceId").GetString()!;
        Assert.Matches("^[0-9a-f]{32}$", unansweredOrderTraceId);
        await FindSingleFixMessageAsync("ordergenerator", "D", unansweredOrderTraceId, senderCompId: "ORDERGENERATOR");
        var unansweredOrderErrorLine = Assert.Single(await ReadServiceJsonLogLinesAsync("ordergenerator"),
            serviceLogLine => serviceLogLine.LogLevel is "Warning" or "Error" && serviceLogLine.ReadLogField("ErrorCode") == expectedProblemType);
        Assert.Equal(("Warning", "Expected error in request."), (unansweredOrderErrorLine.LogLevel, unansweredOrderErrorLine.Message));
        Assert.Equal(unansweredOrderTraceId, unansweredOrderErrorLine.TraceId);
        Assert.Equal(unansweredOrderTraceId, unansweredOrderErrorLine.ReadLogField("dd_trace_id"));
    }

    // RF-04/CA-5 with the real Datadog tracer, for errors that are not of an order: the traceId of the answer is the
    // trace the Datadog sees, the same TraceId and dd_trace_id of the one log line of the error. The ASP.NET request
    // Activity has another id, which the support would not find in the APM.
    [Theory]
    [InlineData("/api/nada", HttpStatusCode.NotFound, "urn:base-investimentos:problem:not-found")]
    [InlineData("/api/orders?page=0", HttpStatusCode.BadRequest, "urn:base-investimentos:problem:invalid-page")]
    public async Task Api_error_answers_and_logs_the_trace_id_of_the_real_tracer(string apiPath, HttpStatusCode expectedHttpStatus, string expectedProblemType)
    {
        using var apiErrorResponse = await composeUnderTest.OrderGeneratorHttp.GetAsync(apiPath);
        var apiErrorTraceId = await ReadProblemTraceIdAsync(apiErrorResponse, expectedHttpStatus, expectedProblemType);

        await AssertSingleErrorLineCarriesTheTraceIdAsync("ordergenerator", apiErrorTraceId, "Warning", "Expected error in request.", expectedProblemType);
    }

    // CA-31 and decision 13 on the API: with the OrderAccumulator paused the OrderGenerator still reads the exposure and
    // the orders from the PostgreSQL; only the order sending depends on the OrderAccumulator.
    [Fact]
    public async Task With_the_orderaccumulator_paused_exposures_and_orders_still_answer_from_postgres()
    {
        HttpStatusCode exposuresStatus;
        HttpStatusCode ordersPageStatus;
        string[] exposureSymbols;
        int ordersPageNumber;
        await composeUnderTest.RunComposeCommandAsync(TimeSpan.FromMinutes(1), "pause", "orderaccumulator");
        try
        {
            using var exposuresResponse = await composeUnderTest.OrderGeneratorHttp.GetAsync("/api/exposures");
            exposuresStatus = exposuresResponse.StatusCode;
            exposureSymbols = ReadSuccessData(await exposuresResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("exposures").EnumerateArray()
                .Select(symbolExposureRow => symbolExposureRow.GetProperty("symbol").GetString()!).ToArray();
            using var ordersPageResponse = await composeUnderTest.OrderGeneratorHttp.GetAsync("/api/orders?page=1");
            ordersPageStatus = ordersPageResponse.StatusCode;
            ordersPageNumber = ReadSuccessData(await ordersPageResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("page").GetInt32();
        }
        finally
        {
            await composeUnderTest.RunComposeCommandAsync(TimeSpan.FromMinutes(1), "unpause", "orderaccumulator");
        }

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (exposuresStatus, ordersPageStatus));
        Assert.Equal(new[] { "PETR4", "VALE3", "VIIA4" }, exposureSymbols);
        Assert.Equal(1, ordersPageNumber);
    }

    // The exception path of the OrderGenerator handler, outside an order: with the Postgres stopped the page read fails
    // and the 500 leaves one Error line in the trace the answer carries.
    [Fact]
    public async Task OrderGenerator_500_without_postgres_answers_and_logs_the_trace_id_of_the_real_tracer()
    {
        const string expectedProblemType = "urn:base-investimentos:problem:internal-error";
        string ordersPageTraceId;
        await composeUnderTest.RunComposeCommandAsync(TimeSpan.FromMinutes(1), "stop", "postgres");
        try
        {
            using var ordersPageResponse = await composeUnderTest.OrderGeneratorHttp.GetAsync("/api/orders?page=1");
            ordersPageTraceId = await ReadProblemTraceIdAsync(ordersPageResponse, HttpStatusCode.InternalServerError, expectedProblemType);
        }
        finally
        {
            await composeUnderTest.RunComposeCommandAsync(TimeSpan.FromMinutes(2), "up", "-d", "--wait", "postgres");
            await WaitForOrdersPageAndOrderDecisionAfterPostgresRestartAsync();
        }

        await AssertSingleErrorLineCarriesTheTraceIdAsync("ordergenerator", ordersPageTraceId, "Error", "Unexpected application error.", expectedProblemType);
    }

    // CA-7 and CA-17: the exposure lives only in the database. Near the limit the next big sale is rejected; after
    // DELETE /api/orders on the OrderGenerator the exposure is zero and the OrderAccumulator accepts the same big sale.
    [Fact]
    public async Task Delete_on_the_OrderGenerator_zeroes_the_exposure_the_OrderAccumulator_decides_with()
    {
        await DeleteAllOrdersThroughTheOrderGeneratorAsync();
        try
        {
            var firstBigSale = await PostOrderAsync("VIIA4", "sell", 99999, 999.99m);
            Assert.Equal("accepted", firstBigSale.GetProperty("status").GetString());
            var (secondBigSale, _) = await PostOrderReadingTheMessageAsync("VIIA4", "sell", 99999, 999.99m);
            Assert.Equal("rejected", secondBigSale.GetProperty("status").GetString());

            await DeleteAllOrdersThroughTheOrderGeneratorAsync();
            Assert.Equal([0m, 0m, 0m], await ReadSymbolExposuresAsync());
            var ordersPageAfterDeletion = ReadSuccessData(await composeUnderTest.OrderGeneratorHttp.GetFromJsonAsync<JsonElement>("/api/orders?page=1"));
            Assert.Equal(0, ordersPageAfterDeletion.GetProperty("total").GetInt64());

            var bigSaleAfterDeletion = await PostOrderAsync("VIIA4", "sell", 99999, 999.99m);
            Assert.Equal("accepted", bigSaleAfterDeletion.GetProperty("status").GetString());
            Assert.Equal(-99_998_000.01m, await ReadSymbolExposureAsync("VIIA4"));
        }
        finally
        {
            await DeleteAllOrdersThroughTheOrderGeneratorAsync();
        }
    }

    // CA-42 and decision 19: the delete runs on the OrderGenerator and the decisions on the OrderAccumulator, two
    // processes, with no lock in memory between them. With orders and a delete at the same time, repeated, at the end
    // the exposure of each symbol is the sum of the accepted orders that stayed stored.
    [Fact]
    public async Task Delete_and_orders_at_the_same_time_end_with_the_exposure_equal_to_the_stored_accepted_orders()
    {
        const int ConcurrencyRounds = 5;
        const int OrdersPerRound = 30;
        string[] roundSymbols = ["PETR4", "VALE3", "VIIA4"];
        var roundRandom = new Random(4242);
        try
        {
            for (var concurrencyRound = 1; concurrencyRound <= ConcurrencyRounds; concurrencyRound++)
            {
                var roundOrders = Enumerable.Range(0, OrdersPerRound).Select(orderIndex => new
                {
                    symbol = roundSymbols[orderIndex % roundSymbols.Length],
                    side = roundRandom.Next(2) == 0 ? "buy" : "sell",
                    quantity = orderIndex % 5 == 0 ? 99999 : roundRandom.Next(1, 1000),
                    price = orderIndex % 5 == 0 ? 999.99m : 10.01m
                }).ToList();

                var roundOrderCalls = roundOrders.Select(async roundOrder =>
                {
                    using var orderResponse = await composeUnderTest.OrderGeneratorHttp.PostAsJsonAsync("/api/orders", roundOrder);
                    return orderResponse.StatusCode;
                }).ToList();
                await Task.Delay(TimeSpan.FromMilliseconds(roundRandom.Next(20, 200)));
                using var roundDeletionResponse = await composeUnderTest.OrderGeneratorHttp.DeleteAsync("/api/orders");
                var roundOrderStatuses = await Task.WhenAll(roundOrderCalls);

                Assert.Equal(HttpStatusCode.NoContent, roundDeletionResponse.StatusCode);
                Assert.All(roundOrderStatuses, roundOrderStatus => Assert.Equal(HttpStatusCode.OK, roundOrderStatus));
                var acceptedOrdersExposureBySymbol = await ReadStoredAcceptedOrdersExposureBySymbolAsync();
                var symbolExposures = await ReadSymbolExposuresAsync();
                Assert.True(
                    roundSymbols.Select(roundSymbol => acceptedOrdersExposureBySymbol.GetValueOrDefault(roundSymbol)).SequenceEqual(symbolExposures),
                    $"round {concurrencyRound}: stored accepted orders {string.Join(" ", acceptedOrdersExposureBySymbol)}, exposure {string.Join(" ", symbolExposures)}");
            }
        }
        finally
        {
            await DeleteAllOrdersThroughTheOrderGeneratorAsync();
        }
    }

    private static async Task<string> ReadProblemTraceIdAsync(HttpResponseMessage apiErrorResponse, HttpStatusCode expectedHttpStatus, string expectedProblemType)
    {
        Assert.Equal(expectedHttpStatus, apiErrorResponse.StatusCode);
        Assert.Equal("application/problem+json", apiErrorResponse.Content.Headers.ContentType?.MediaType);
        var apiErrorProblem = await apiErrorResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expectedProblemType, apiErrorProblem.GetProperty("type").GetString());
        var apiErrorTraceId = apiErrorProblem.GetProperty("traceId").GetString()!;
        Assert.Matches("^[0-9a-f]{32}$", apiErrorTraceId);
        return apiErrorTraceId;
    }

    // Exactly one Warning or Error in the trace of the answer, and the tracer injected that same trace as dd_trace_id.
    private async Task AssertSingleErrorLineCarriesTheTraceIdAsync(
        string serviceName, string apiErrorTraceId, string expectedLogLevel, string expectedLogMessage, string expectedProblemType)
    {
        var apiErrorLine = Assert.Single(await ReadServiceJsonLogLinesAsync(serviceName),
            serviceLogLine => serviceLogLine.LogLevel is "Warning" or "Error" && serviceLogLine.TraceId == apiErrorTraceId);
        Assert.Equal((expectedLogLevel, expectedLogMessage, expectedProblemType),
            (apiErrorLine.LogLevel, apiErrorLine.Message, apiErrorLine.ReadLogField("ErrorCode")));
        Assert.Equal(apiErrorTraceId, apiErrorLine.ReadLogField("dd_trace_id"));
    }

    private async Task DeleteAllOrdersThroughTheOrderGeneratorAsync()
    {
        using var ordersDeletionResponse = await composeUnderTest.OrderGeneratorHttp.DeleteAsync("/api/orders");
        Assert.Equal(HttpStatusCode.NoContent, ordersDeletionResponse.StatusCode);
    }

    private async Task<decimal[]> ReadSymbolExposuresAsync()
    {
        var exposuresBody = ReadSuccessData(await composeUnderTest.OrderGeneratorHttp.GetFromJsonAsync<JsonElement>("/api/exposures"));
        return exposuresBody.GetProperty("exposures").EnumerateArray().Select(symbolExposureRow => symbolExposureRow.GetProperty("exposure").GetDecimal()).ToArray();
    }

    // The Postgres port is not open on the host: the sum is read by psql inside its container, straight from the orders table.
    private async Task<Dictionary<string, decimal>> ReadStoredAcceptedOrdersExposureBySymbolAsync()
    {
        var acceptedOrdersExposureRows = await composeUnderTest.RunComposeCommandAsync(TimeSpan.FromSeconds(30), "exec", "-T", "postgres",
            "psql", "-U", "flowa", "-d", "flowa", "-At", "-F", ";", "-c",
            "SELECT symbol, sum(CASE WHEN side = '1' THEN price * quantity ELSE -(price * quantity) END) FROM orders WHERE accepted GROUP BY symbol");
        return acceptedOrdersExposureRows
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(acceptedOrdersExposureRow => acceptedOrdersExposureRow.Split(';'))
            .ToDictionary(symbolAndExposure => symbolAndExposure[0], symbolAndExposure => decimal.Parse(symbolAndExposure[1], System.Globalization.CultureInfo.InvariantCulture));
    }

    // The tests after this one send orders, which the OrderAccumulator stores in the Postgres. The OrderAccumulator only
    // notices that the Postgres restarted on its next order (57P01 on a pooled connection, P04-7), so the wait ends with a
    // tiny order decided: the orders page answers and the OrderAccumulator decides again.
    private async Task WaitForOrdersPageAndOrderDecisionAfterPostgresRestartAsync()
    {
        var postgresBackDeadline = DateTime.UtcNow.AddMinutes(1);
        while (DateTime.UtcNow < postgresBackDeadline)
        {
            using var ordersPageResponse = await composeUnderTest.OrderGeneratorHttp.GetAsync("/api/orders?page=1");
            if (ordersPageResponse.StatusCode == HttpStatusCode.OK)
            {
                using var probeOrderResponse = await composeUnderTest.OrderGeneratorHttp.PostAsJsonAsync(
                    "/api/orders", new { symbol = "PETR4", side = "buy", quantity = 1, price = 0.01m });
                if (probeOrderResponse.StatusCode == HttpStatusCode.OK)
                    return;
            }
            await Task.Delay(500);
        }
        throw new TimeoutException("the orders page and the order decision did not come back within 1 minute after the Postgres came back");
    }

    private async Task<decimal> ReadSymbolExposureAsync(string symbol)
    {
        var exposuresBody = ReadSuccessData(await composeUnderTest.OrderGeneratorHttp.GetFromJsonAsync<JsonElement>("/api/exposures"));
        var symbolExposureRow = Assert.Single(exposuresBody.GetProperty("exposures").EnumerateArray(),
            exposureRowOfSymbol => exposureRowOfSymbol.GetProperty("symbol").GetString() == symbol);
        return symbolExposureRow.GetProperty("exposure").GetDecimal();
    }

    private async Task<JsonElement> PostOrderWithCallerTraceParentAsync(string callerTraceParent)
    {
        using var orderHttpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = JsonContent.Create(new { symbol = "PETR4", side = "buy", quantity = 1, price = 1.00m })
        };
        orderHttpRequest.Headers.Add("traceparent", callerTraceParent);
        using var createOrderResponse = await composeUnderTest.OrderGeneratorHttp.SendAsync(orderHttpRequest);
        Assert.Equal(HttpStatusCode.OK, createOrderResponse.StatusCode);
        var orderResponse = ReadSuccessData(await createOrderResponse.Content.ReadFromJsonAsync<JsonElement>());
        Assert.Equal("accepted", orderResponse.GetProperty("status").GetString());
        return orderResponse;
    }

    private async Task<JsonElement> PostOrderAsync(string symbol, string side, int quantity, decimal price) =>
        (await PostOrderReadingTheMessageAsync(symbol, side, quantity, price)).Order;

    // The answer of an order is a DataMessage: the order is its "data" and the text the screen shows is its "message".
    private async Task<(JsonElement Order, string OrderMessage)> PostOrderReadingTheMessageAsync(string symbol, string side, int quantity, decimal price)
    {
        using var createOrderResponse = await composeUnderTest.OrderGeneratorHttp.PostAsJsonAsync("/api/orders", new { symbol, side, quantity, price });
        Assert.Equal(HttpStatusCode.OK, createOrderResponse.StatusCode);
        var orderDataMessage = await createOrderResponse.Content.ReadFromJsonAsync<JsonElement>();
        return (ReadSuccessData(orderDataMessage), orderDataMessage.GetProperty("message").GetString()!);
    }

    private static JsonElement ReadSuccessData(JsonElement successDataMessage)
    {
        Assert.True(successDataMessage.GetProperty("success").GetBoolean());
        Assert.Equal("Ok", successDataMessage.GetProperty("status").GetString());
        return successDataMessage.GetProperty("data");
    }

    private async Task<FixMessage> FindSingleFixMessageAsync(string serviceName, string fixMsgType, string clOrdId, string senderCompId)
    {
        var matchingFixMessages = FixLog.ParseFixMessages(await composeUnderTest.ReadServiceLogAsync(serviceName))
            .Where(fixMessage => fixMessage.ReadFixTagValue(35) == fixMsgType && fixMessage.ReadFixTagValue(11) == clOrdId && fixMessage.ReadFixTagValue(49) == senderCompId)
            .ToList();
        return Assert.Single(matchingFixMessages);
    }

    private async Task<IReadOnlyList<ComposeJsonLogLine>> ReadServiceJsonLogLinesAsync(string serviceName) =>
        (await composeUnderTest.ReadServiceStdoutAsync(serviceName))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ComposeJsonLogLine.ParseContainerLogLine)
            .ToList();

    private async Task<IReadOnlyList<ComposeJsonLogLine>> ReadOrderLogLinesAsync(string serviceName, string clOrdId) =>
        (await ReadServiceJsonLogLinesAsync(serviceName)).Where(serviceLogLine => serviceLogLine.TraceId == clOrdId).ToList();
}

// Runs without Docker: the limit is a constant of the OrderAccumulator and must not leak into configuration (CA-21).
public sealed class ExposureLimitOutsideConfigTests
{
    private static readonly Regex ExposureLimitPattern = new(@"100[.,_ ]?000[.,_ ]?000|\b1(\.0+)?e\+?0*8\b", RegexOptions.IgnoreCase);

    [Theory]
    [InlineData("100000000")]
    [InlineData("100.000.000")]
    [InlineData("100,000,000")]
    [InlineData("100_000_000")]
    [InlineData("100 000 000")]
    [InlineData("100000000.00")]
    [InlineData("1e8")]
    [InlineData("1E+08")]
    [InlineData("1.0e8")]
    [InlineData("Exposure__Limit: \"1.0E8\"")]
    public void Guard_finds_the_limit_in_every_spelling(string limitSpelling) =>
        Assert.Matches(ExposureLimitPattern, limitSpelling);

    [Theory]
    [InlineData("99999999")]
    [InlineData("10000000")]
    [InlineData("1.5e8")]
    [InlineData("Port=5432")]
    public void Guard_does_not_mistake_other_numbers_for_the_limit(string numberThatIsNotTheLimit) =>
        Assert.DoesNotMatch(ExposureLimitPattern, numberThatIsNotTheLimit);

    [Fact]
    public void Exposure_limit_does_not_appear_in_the_compose_the_Dockerfiles_or_appsettings()
    {
        var repoRoot = RepoPaths.FindRepoRoot();
        var packagingFiles = new[] { "docker-compose.yml", "src/flowa.ordergenerator-webapi-ecs/Dockerfile", "src/flowa.orderaccumulator-worker-ecs/Dockerfile" }
            .Select(packagingRelativePath => Path.Combine(repoRoot, packagingRelativePath))
            .ToList();
        var appSettingsFiles = Directory.EnumerateFiles(Path.Combine(repoRoot, "src"), "appsettings*.json", SearchOption.AllDirectories)
            .Where(appSettingsPath => !appSettingsPath.Split(Path.DirectorySeparatorChar).Any(pathSegment => pathSegment is "bin" or "obj"))
            .ToList();

        Assert.All(packagingFiles, packagingPath => Assert.True(File.Exists(packagingPath), $"{packagingPath} does not exist"));
        Assert.Contains(appSettingsFiles, appSettingsPath => appSettingsPath.Contains("flowa.orderaccumulator-worker-ecs"));

        var configFilesWithLimit = packagingFiles.Concat(appSettingsFiles)
            .Where(checkedConfigPath => ExposureLimitPattern.IsMatch(File.ReadAllText(checkedConfigPath)))
            .Select(checkedConfigPath => Path.GetRelativePath(repoRoot, checkedConfigPath))
            .ToList();
        Assert.Empty(configFilesWithLimit);
    }
}

// Runs without Docker. The .git stays in the context on purpose: the build reads its commit.
public sealed class DockerBuildContextTests
{
    [Fact]
    public void Dockerignore_removes_only_build_outputs_and_local_files()
    {
        var dockerignorePatterns = File.ReadAllLines(Path.Combine(RepoPaths.FindRepoRoot(), ".dockerignore"))
            .Select(dockerignoreLine => dockerignoreLine.Trim())
            .Where(dockerignoreLine => dockerignoreLine.Length > 0 && !dockerignoreLine.StartsWith('#'))
            .ToArray();

        Assert.Equal(
            new[]
            {
                ".vs/", ".vscode/", ".idea/",
                "**/bin/", "**/obj/", "**/node_modules/", "**/dist/",
                "**/TestResults/", "**/playwright-report/", "**/test-results/",
                "src/flowa.ordergenerator-webapi-ecs/wwwroot/",
                ".env", ".env.*", "*.log",
            },
            dockerignorePatterns);
    }
}

// Acceptance finding: a clone of a new commit, started with the same project name, reused
// the image of the previous commit and /version lied. The compose now always rebuilds on up.
[Collection(ComposeCollection.CollectionName)]
[Trait("Category", "Integration")]
public sealed class CleanCloneImageCommitTests
{
    private const string CloneComposeProjectName = "flowa-it-commit";
    private const int CloneOrderGeneratorHostPort = 18093;
    private static readonly TimeSpan CloneComposeUpTimeout = TimeSpan.FromMinutes(15);

    [Fact]
    public async Task Docker_compose_up_of_a_clone_serves_the_current_commit_even_with_the_image_of_the_previous_commit()
    {
        var repoRoot = RepoPaths.FindRepoRoot();
        var cloneDirectory = Path.Combine(Path.GetTempPath(), $"flowa-it-commit-{Guid.NewGuid():N}");
        var cloneComposeFile = Path.Combine(cloneDirectory, "docker-compose.yml");
        // Without SOURCE_REVISION_ID: the clone has a real .git, like the evaluator's.
        var cloneEnvironmentVariables = new Dictionary<string, string?>
        {
            ["FLOWA_HTTP_PORT"] = CloneOrderGeneratorHostPort.ToString(), ["SOURCE_REVISION_ID"] = null,
            ["GIT_AUTHOR_NAME"] = "flowa-it", ["GIT_AUTHOR_EMAIL"] = "flowa-it@localhost",
            ["GIT_COMMITTER_NAME"] = "flowa-it", ["GIT_COMMITTER_EMAIL"] = "flowa-it@localhost",
        };
        Task<string> CaptureCloneExternalCommandOutputAsync(TimeSpan commandTimeout, string commandExecutable, params string[] commandArguments) =>
            ExternalCommand.CaptureExternalCommandOutputAsync(commandExecutable, commandTimeout, cloneEnvironmentVariables, commandArguments);
        Task<string> RunCloneComposeCommandAsync(TimeSpan commandTimeout, params string[] composeArguments) =>
            CaptureCloneExternalCommandOutputAsync(commandTimeout, "docker", ["compose", "-p", CloneComposeProjectName, "-f", cloneComposeFile, .. composeArguments]);

        // The GET /version leaves from inside each container, through the /dev/tcp of bash: the aspnet image has no
        // curl and port 8081 of the OrderAccumulator does not even leave the compose network.
        async Task<string> ReadCloneServiceCommitAsync(string serviceName, int containerHttpPort)
        {
            var lastVersionFailure = "no answer";
            var versionDeadline = DateTime.UtcNow.AddMinutes(2);
            while (DateTime.UtcNow < versionDeadline)
            {
                try
                {
                    var versionHttpResponse = await RunCloneComposeCommandAsync(TimeSpan.FromSeconds(20), "exec", "-T", serviceName, "bash", "-c",
                        $"exec 3<>/dev/tcp/127.0.0.1/{containerHttpPort} && printf 'GET /version HTTP/1.0\r\nHost: localhost\r\n\r\n' >&3 && cat <&3");
                    var versionJsonStart = versionHttpResponse.IndexOf('{');
                    if (versionJsonStart >= 0)
                    {
                        using var versionDocument = JsonDocument.Parse(versionHttpResponse[versionJsonStart..]);
                        return versionDocument.RootElement.GetProperty("commit").GetString()!;
                    }
                    lastVersionFailure = versionHttpResponse;
                }
                catch (Exception versionFailure) when (versionFailure is InvalidOperationException or TimeoutException)
                {
                    lastVersionFailure = versionFailure.Message;
                }
                await Task.Delay(500);
            }
            throw new TimeoutException($"the /version of {serviceName} did not answer within 2 minutes; last failure: {lastVersionFailure}");
        }

        var repoHeadCommit = (await CaptureCloneExternalCommandOutputAsync(TimeSpan.FromSeconds(30), "git", "-C", repoRoot, "rev-parse", "HEAD")).Trim();
        var cloneTestFailed = false;
        try
        {
            await CaptureCloneExternalCommandOutputAsync(TimeSpan.FromMinutes(2), "git", "clone", "--quiet", "--no-local", repoRoot, cloneDirectory);
            await CaptureCloneExternalCommandOutputAsync(TimeSpan.FromSeconds(30), "git", "-C", cloneDirectory, "checkout", "--quiet", repoHeadCommit);

            await RunCloneComposeCommandAsync(CloneComposeUpTimeout, "up", "-d", "--wait", "--wait-timeout", "300");
            Assert.Equal(repoHeadCommit, await ReadCloneServiceCommitAsync("ordergenerator", 8080));
            Assert.Equal(repoHeadCommit, await ReadCloneServiceCommitAsync("orderaccumulator", 8081));
            await RunCloneComposeCommandAsync(TimeSpan.FromMinutes(2), "down", "-v");

            await CaptureCloneExternalCommandOutputAsync(TimeSpan.FromSeconds(30), "git", "-C", cloneDirectory, "commit", "--allow-empty", "--quiet", "-m", "new test commit");
            var newCloneCommit = (await CaptureCloneExternalCommandOutputAsync(TimeSpan.FromSeconds(30), "git", "-C", cloneDirectory, "rev-parse", "HEAD")).Trim();
            Assert.NotEqual(repoHeadCommit, newCloneCommit);

            await RunCloneComposeCommandAsync(CloneComposeUpTimeout, "up", "-d", "--wait", "--wait-timeout", "300");
            Assert.Equal(newCloneCommit, await ReadCloneServiceCommitAsync("ordergenerator", 8080));
            Assert.Equal(newCloneCommit, await ReadCloneServiceCommitAsync("orderaccumulator", 8081));
        }
        catch
        {
            cloneTestFailed = true;
            throw;
        }
        finally
        {
            // Every cleanup step runs; with the test already failed, a cleanup failure only goes to the log.
            Func<Task>[] cloneCleanupSteps =
            [
                async () => { if (File.Exists(cloneComposeFile)) await RunCloneComposeCommandAsync(TimeSpan.FromMinutes(2), "down", "-v", "--rmi", "local"); },
                () => { DeleteCloneDirectory(cloneDirectory); return Task.CompletedTask; },
            ];
            var cloneCleanupFailures = new List<Exception>();
            foreach (var cloneCleanupStep in cloneCleanupSteps)
            {
                try { await cloneCleanupStep(); }
                catch (Exception cloneCleanupFailure) { cloneCleanupFailures.Add(cloneCleanupFailure); }
            }
            if (cloneCleanupFailures.Count > 0 && !cloneTestFailed) throw new AggregateException("the clone cleanup failed", cloneCleanupFailures);
            foreach (var cloneCleanupFailure in cloneCleanupFailures)
                Console.Error.WriteLine($"clone cleanup failed after the test failure: {cloneCleanupFailure.Message}");
        }
    }

    // Git objects are read-only on Windows; without clearing the attribute, Delete fails.
    private static void DeleteCloneDirectory(string cloneDirectory)
    {
        if (!Directory.Exists(cloneDirectory)) return;
        foreach (var cloneFile in Directory.EnumerateFiles(cloneDirectory, "*", SearchOption.AllDirectories))
            File.SetAttributes(cloneFile, FileAttributes.Normal);
        Directory.Delete(cloneDirectory, recursive: true);
    }
}
