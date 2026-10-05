using System.Globalization;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net;
using System.Text.Json;
using Base.OrderAccumulator.Domain.Orders;
using Base.OrderAccumulator.Entrypoint.Fix;
using Base.OrderAccumulator.Entrypoint.Workers;
using Base.OrderAccumulator.Entrypoint;
using Base.OrderAccumulator.Infrastructure.Fix;
using Base.OrderAccumulator.Infrastructure.Logging;
using Base.OrderAccumulator.Infrastructure.Persistence;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using QuickFix.Fields;
using QuickFix.FIX44;
using QuickFix;
using Xunit.Abstractions;

namespace Base.OrderAccumulator.Tests;

// CA-8, CA-9, CA-13, CA-18 e CA-19 da F3: as duas pontas QuickFIX/n de verdade, com o PostgreSQL do container.
[Collection(OrderAccumulatorPostgresCollection.Name)]
public sealed class FixAcceptorTests(OrderAccumulatorPostgresFixture orderAccumulatorDatabase, ITestOutputHelper fixLogTestOutput) : IAsyncLifetime
{
    public Task InitializeAsync() => orderAccumulatorDatabase.ResetOrdersAndExposuresAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Accepted_order_gets_execution_report_new_with_every_contract_tag()
    {
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();
        using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort);
        var acceptedOrder = FixTestInitiator.NewOrder("aceita-ca8", "PETR4", '1', 100, 10.50m);

        var orderExecutionReport = await fixTestInitiator.SendExpectingExecutionReportAsync(acceptedOrder);

        var storedOrderExecutionIds = await ReadStoredOrderExecutionIdsAsync("aceita-ca8");
        Assert.Equal(
            $"35=8|37={storedOrderExecutionIds.OrderId}|17={storedOrderExecutionIds.ExecId}|150=0|39=0|11=aceita-ca8|55=PETR4|54=1|151=100|14=0|6=0|58=",
            FormatExecutionReportContractTags(orderExecutionReport));
        Assert.Equal(1_050.00m, await orderAccumulatorDatabase.ReadExposureOfSymbolAsync("PETR4"));
    }

    [Fact]
    public async Task Order_over_the_limit_gets_execution_report_rejected_with_the_reason()
    {
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();
        using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort);
        // 2 × 50.000 × 999,99 = 99.999.000; mais 2.000 × 1,00 passaria de 100.000.000.
        await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewBuyOrder("VALE3", 50_000, 999.99m));
        await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewBuyOrder("VALE3", 50_000, 999.99m));
        var overLimitOrder = FixTestInitiator.NewOrder("estoura-ca9-compra", "VALE3", '1', 2_000, 1.00m);

        var orderExecutionReport = await fixTestInitiator.SendExpectingExecutionReportAsync(overLimitOrder);

        var storedOrderExecutionIds = await ReadStoredOrderExecutionIdsAsync("estoura-ca9-compra");
        Assert.Equal(
            $"35=8|37={storedOrderExecutionIds.OrderId}|17={storedOrderExecutionIds.ExecId}|150=8|39=8|11=estoura-ca9-compra|55=VALE3|54=1|151=0|14=0|6=0" +
            "|58=Ordem rejeitada: a exposição de VALE3 passaria do limite de 100.000.000,00.",
            FormatExecutionReportContractTags(orderExecutionReport));
        Assert.Equal(99_999_000.00m, await orderAccumulatorDatabase.ReadExposureOfSymbolAsync("VALE3"));
    }

    public static TheoryData<string, char, string, string, string> InvalidOrdersSentByFix => new()
    {
        { "ABCD3", '1', "100", "10.00", "Símbolo inválido. Use PETR4, VALE3 ou VIIA4." },
        { "PETR4", '3', "100", "10.00", "Lado inválido. Use compra ou venda." },
        { "PETR4", '1', "0", "10.00", "A quantidade deve ser maior que zero." },
        { "PETR4", '1', "100000", "10.00", "A quantidade deve ser menor que 100.000." },
        { "PETR4", '1', "1.5", "10.00", "A quantidade deve ser um número inteiro." },
        { "PETR4", '1', "100", "0", "O preço deve ser maior que zero." },
        { "PETR4", '1', "100", "1000", "O preço deve ser menor que 1.000,00." },
        { "PETR4", '1', "100", "10.001", "O preço deve ser múltiplo de 0,01." },
        { "XPTO4", '1', "100", "1000", "Símbolo inválido. Use PETR4, VALE3 ou VIIA4. O preço deve ser menor que 1.000,00." }
    };

    [Theory]
    [MemberData(nameof(InvalidOrdersSentByFix))]
    public async Task Invalid_order_sent_straight_by_fix_is_rejected_and_leaves_exposure_untouched(
        string symbol, char side, string quantity, string price, string expectedRejectionText)
    {
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();
        using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort);
        var invalidOrder = FixTestInitiator.NewOrder("invalida-ca13", symbol, side,
            decimal.Parse(quantity, CultureInfo.InvariantCulture), decimal.Parse(price, CultureInfo.InvariantCulture));

        var orderExecutionReport = await fixTestInitiator.SendExpectingExecutionReportAsync(invalidOrder);

        var storedOrderExecutionIds = await ReadStoredOrderExecutionIdsAsync("invalida-ca13");
        Assert.Equal(
            $"35=8|37={storedOrderExecutionIds.OrderId}|17={storedOrderExecutionIds.ExecId}|150=8|39=8|11=invalida-ca13|55={symbol}|54={side}|151=0|14=0|6=0|58={expectedRejectionText}",
            FormatExecutionReportContractTags(orderExecutionReport));

        var exposuresResponse = await orderAccumulatorTestApp.CreateClient().GetFromJsonAsync<JsonElement>("/api/exposures");
        Assert.Equal([0m, 0m, 0m], ReadExposuresData(exposuresResponse).Exposures.Select(symbolExposure => symbolExposure.Exposure));
    }

    [Fact]
    public async Task Repeated_cl_ord_id_returns_the_original_report_and_counts_once()
    {
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();
        using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort);

        var firstOrderExecutionReport = await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder("repetida", "VIIA4", '1', 10, 5.00m));
        var repeatedOrderExecutionReport = await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder("repetida", "VIIA4", '1', 10, 5.00m));

        Assert.Equal(FormatExecutionReportContractTags(firstOrderExecutionReport), FormatExecutionReportContractTags(repeatedOrderExecutionReport));
        Assert.Equal(ExecType.NEW, repeatedOrderExecutionReport.ExecType.Value);
        Assert.Equal(50.00m, await orderAccumulatorDatabase.ReadExposureOfSymbolAsync("VIIA4"));
        Assert.Equal(1, await orderAccumulatorDatabase.CountStoredOrdersAsync("repetida"));
        Assert.Single(orderAccumulatorTestApp.CapturedOrderAccumulatorLogs.CapturedLogLines, capturedLogLine => capturedLogLine == "Information Base.OrderAccumulator.Entrypoint.Fix.NewOrderSingleConsumer: Repeated ClOrdID: sending the stored answer back.");
    }

    [Fact]
    public async Task Repeated_rejected_order_returns_the_same_rejection_text()
    {
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();
        using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort);

        var firstOrderExecutionReport = await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder("repetida-invalida", "PETR4", '1', 100, 1000m));
        var repeatedOrderExecutionReport = await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder("repetida-invalida", "PETR4", '1', 100, 1000m));

        Assert.Equal(FormatExecutionReportContractTags(firstOrderExecutionReport), FormatExecutionReportContractTags(repeatedOrderExecutionReport));
        Assert.Equal("O preço deve ser menor que 1.000,00.", repeatedOrderExecutionReport.Text.Value);
        Assert.Equal(1, await orderAccumulatorDatabase.CountStoredOrdersAsync("repetida-invalida"));
    }

    [Fact]
    public async Task Restart_keeps_the_exposure_and_a_resent_order_is_not_counted_again()
    {
        ExecutionReport orderExecutionReportBeforeRestart;
        int fixAcceptorPort;
        await using (var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor())
        using (var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort))
        {
            fixAcceptorPort = orderAccumulatorTestApp.FixAcceptorPort;
            orderExecutionReportBeforeRestart = await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder("antes-do-reinicio", "VALE3", '1', 100, 10.00m));
        }

        // Volta na MESMA porta: só sobe se a parada anterior fechou o acceptor.
        await using var restartedOrderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString, fixAcceptorPort).StartWithFixAcceptor();
        var exposuresResponse = await restartedOrderAccumulatorTestApp.CreateClient().GetFromJsonAsync<JsonElement>("/api/exposures");
        Assert.Equal(1_000.00m, ReadExposuresData(exposuresResponse).Exposures.Single(symbolExposure => symbolExposure.Symbol == "VALE3").Exposure);

        using var fixTestInitiatorAfterRestart = await FixTestInitiator.LogOnToAcceptorAsync(restartedOrderAccumulatorTestApp.FixAcceptorPort);
        var resentOrderExecutionReport = await fixTestInitiatorAfterRestart.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder("antes-do-reinicio", "VALE3", '1', 100, 10.00m));

        Assert.Equal(FormatExecutionReportContractTags(orderExecutionReportBeforeRestart), FormatExecutionReportContractTags(resentOrderExecutionReport));
        Assert.Equal(1_000.00m, await orderAccumulatorDatabase.ReadExposureOfSymbolAsync("VALE3"));
        Assert.Equal(1, await orderAccumulatorDatabase.CountStoredOrdersAsync("antes-do-reinicio"));
    }

    [Fact]
    public async Task Fix_heartbeats_are_exchanged_but_never_logged()
    {
        // Decision 22. Positive control: with a 1 s interval the acceptor really sends heartbeats during the test.
        using var stdoutJsonLogCapture = new StdoutJsonLogCapture();
        int heartbeatsSentByTheAcceptor;
        await using (var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor())
        {
            using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort, heartbeatIntervalSeconds: 1);
            var heartbeatClock = System.Diagnostics.Stopwatch.StartNew();
            while (fixTestInitiator.ReceivedHeartbeatCount < 2 && heartbeatClock.Elapsed < TimeSpan.FromSeconds(10))
                await Task.Delay(100);
            heartbeatsSentByTheAcceptor = fixTestInitiator.ReceivedHeartbeatCount;
        }

        Assert.True(heartbeatsSentByTheAcceptor >= 2, $"the acceptor sent {heartbeatsSentByTheAcceptor} heartbeats in 10 s");
        Assert.Contains(stdoutJsonLogCapture.JsonLogLines, jsonLogLine => jsonLogLine.ReadLogField("FixMessage")?.Contains("|35=A|") == true);
        Assert.DoesNotContain(stdoutJsonLogCapture.JsonLogLines, jsonLogLine => jsonLogLine.ReadLogField("FixMessage")?.Contains("|35=0|") == true);
    }

    [Fact]
    public async Task Fix_messages_in_and_out_are_written_to_stdout_as_json_lines()
    {
        // CA-7: reads the real stdout of the app, every line one JSON log line.
        using (var stdoutJsonLogCapture = new StdoutJsonLogCapture())
        {
            await using (var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor())
            {
                using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort);
                await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder("log-ca19", "PETR4", '2', 5, 20.00m));
            }

            foreach (var stdoutLine in stdoutJsonLogCapture.StdoutLines)
                fixLogTestOutput.WriteLine(stdoutLine);

            const string fixSessionLogCategory = "Base.OrderAccumulator.Infrastructure.Fix.FixSessionLog";
            const string acceptorFixSession = "FIX.4.4:ORDERACCUMULATOR->ORDERGENERATOR";
            var fixSessionLogLines = stdoutJsonLogCapture.JsonLogLines
                .Where(jsonLogLine => jsonLogLine.Category == fixSessionLogCategory && jsonLogLine.ReadLogField("FixSession") == acceptorFixSession)
                .ToList();
            var receivedOrderLine = Assert.Single(fixSessionLogLines, fixLogLine =>
                fixLogLine.Message == "FIX message received." && fixLogLine.ReadLogField("FixMessage")!.Contains("|35=D|") && fixLogLine.ReadLogField("FixMessage")!.Contains("|11=log-ca19|"));
            var sentExecutionReportLine = Assert.Single(fixSessionLogLines, fixLogLine =>
                fixLogLine.Message == "FIX message sent." && fixLogLine.ReadLogField("FixMessage")!.Contains("|35=8|") && fixLogLine.ReadLogField("FixMessage")!.Contains("|11=log-ca19|"));
            Assert.Equal("Information", receivedOrderLine.LogLevel);
            Assert.Equal("Information", sentExecutionReportLine.LogLevel);
            Assert.StartsWith("8=FIX.4.4|", receivedOrderLine.ReadLogField("FixMessage"));
            Assert.Contains(fixSessionLogLines, fixLogLine => fixLogLine.Message == "Session reset: ResetOnLogon");
            Assert.Contains(fixSessionLogLines, fixLogLine => fixLogLine.Message == "Session reset: ResetOnDisconnect");
            Assert.DoesNotContain(stdoutJsonLogCapture.JsonLogLines, jsonLogLine => jsonLogLine.LogLevel is "Debug" or "Trace");
        }
    }

    [Fact]
    public async Task Database_failure_sends_no_execution_report_logs_the_order_and_keeps_the_fix_session_up()
    {
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString, replaceOrderAccumulatorServices: orderAccumulatorTestServices =>
            orderAccumulatorTestServices.AddScoped<IOrderRepository>(orderOperationServices =>
                new OrderRepositoryFailingForClOrdId("falha-banco", new OrderRepository(orderOperationServices.GetRequiredService<PostgresUnitOfWork>())))).StartWithFixAcceptor();
        using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort);

        await fixTestInitiator.ExpectNoAnswerAsync(FixTestInitiator.NewOrder("falha-banco", "PETR4", '1', 10, 1.00m), TimeSpan.FromSeconds(2));
        var orderExecutionReportAfterDatabaseFailure = await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder("depois-da-falha", "PETR4", '1', 10, 1.00m));

        Assert.Equal(ExecType.NEW, orderExecutionReportAfterDatabaseFailure.ExecType.Value);
        Assert.Single(orderAccumulatorTestApp.CapturedOrderAccumulatorLogs.CapturedLogLines, capturedLogLine => capturedLogLine == "Error Base.OrderAccumulator.Entrypoint.Fix.NewOrderSingleConsumer: Order decision failed; no ExecutionReport sent.");
        Assert.Equal(10.00m, await orderAccumulatorDatabase.ReadExposureOfSymbolAsync("PETR4"));
    }

    [Fact]
    public void Acceptor_session_is_fix44_with_ephemeral_store_reset_on_every_reconnect()
    {
        var loadedFixAcceptorSettings = FixAcceptorWorker.LoadFixAcceptorSessionSettings(BuildFixAcceptorConfiguration(("Fix:AcceptorPort", "19876")));

        var fixSessionId = Assert.Single(loadedFixAcceptorSettings.GetSessions());
        Assert.Equal(new SessionID("FIX.4.4", "ORDERACCUMULATOR", "ORDERGENERATOR"), fixSessionId);
        var fixSessionSettings = loadedFixAcceptorSettings.Get(fixSessionId);
        Assert.Equal("acceptor", fixSessionSettings.GetString("ConnectionType"));
        Assert.Equal(19876, fixSessionSettings.GetInt("SocketAcceptPort"));
        Assert.Equal(["Y", "Y", "Y"], new[] { "ResetOnLogon", "ResetOnLogout", "ResetOnDisconnect" }.Select(fixSessionSettings.GetString));
        Assert.Equal("Y", fixSessionSettings.GetString("UseDataDictionary"));
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "FIX44-flowa.xml"), fixSessionSettings.GetString("DataDictionary"));
        Assert.Equal("Y", fixSessionSettings.GetString("ValidateUserDefinedFields"));
        Assert.Equal("N", fixSessionSettings.GetString("AllowUnknownMsgFields"));
        Assert.True(File.Exists(fixSessionSettings.GetString("DataDictionary")));
        // Sem Fix__AcceptorBindHost o acceptor escuta em todas as interfaces (o compose precisa disso).
        Assert.False(fixSessionSettings.Has("SocketAcceptHost"));
    }

    [Fact]
    public void Bind_host_from_configuration_limits_the_acceptor_to_that_address()
    {
        var loadedFixAcceptorSettings = FixAcceptorWorker.LoadFixAcceptorSessionSettings(
            BuildFixAcceptorConfiguration(("Fix:AcceptorPort", "19876"), ("Fix:AcceptorBindHost", "127.0.0.1")));

        Assert.Equal("127.0.0.1", loadedFixAcceptorSettings.Get(loadedFixAcceptorSettings.GetSessions().Single()).GetString("SocketAcceptHost"));
    }

    [Fact]
    public async Task Test_app_listens_for_fix_only_on_loopback()
    {
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();

        var fixListeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
            .Where(tcpListenerEndpoint => tcpListenerEndpoint.Port == orderAccumulatorTestApp.FixAcceptorPort)
            .ToList();

        Assert.Equal([new IPEndPoint(IPAddress.Loopback, orderAccumulatorTestApp.FixAcceptorPort)], fixListeners);
    }

    [Fact]
    public async Task Order_without_price_gets_business_reject_and_is_not_recorded()
    {
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();
        using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort);
        var orderWithoutPrice = FixTestInitiator.NewOrder("sem-preco", "PETR4", '1', 10, 1.00m);
        orderWithoutPrice.RemoveField(Tags.Price);

        var missingPriceBusinessReject = await fixTestInitiator.SendExpectingBusinessRejectAsync(orderWithoutPrice);
        var orderExecutionReportAfterBusinessReject = await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder("depois-sem-preco", "PETR4", '1', 10, 1.00m));

        Assert.Equal("D", missingPriceBusinessReject.RefMsgType.Value);
        // O QuickFIX/n 1.14.1 não preenche RefTagID (371); o motivo vai em 380 e 58.
        Assert.Equal("Conditionally Required Field Missing", missingPriceBusinessReject.Text.Value);
        Assert.Equal(BusinessRejectReason.CONDITIONALLY_REQUIRED_FIELD_MISSING, missingPriceBusinessReject.BusinessRejectReason.Value);
        Assert.Equal(ExecType.NEW, orderExecutionReportAfterBusinessReject.ExecType.Value);
        Assert.Equal(0, await orderAccumulatorDatabase.CountStoredOrdersAsync("sem-preco"));
        Assert.Equal(10.00m, await orderAccumulatorDatabase.ReadExposureOfSymbolAsync("PETR4"));
    }

    [Fact]
    public async Task Execution_report_that_cannot_be_sent_is_logged_and_the_order_stays_recorded()
    {
        // Sessão do acceptor criada, mas ninguém logado: o SendToTarget devolve false.
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();
        var newOrderSingleConsumer = orderAccumulatorTestApp.Services.GetRequiredService<NewOrderSingleConsumer>();
        var acceptorSessionId = new SessionID("FIX.4.4", "ORDERACCUMULATOR", "ORDERGENERATOR");

        newOrderSingleConsumer.OnMessage(FixTestInitiator.NewOrder("sem-sessao", "VIIA4", '1', 10, 2.00m), acceptorSessionId);

        Assert.Single(orderAccumulatorTestApp.CapturedOrderAccumulatorLogs.CapturedLogLines, capturedLogLine => capturedLogLine == "Warning Base.OrderAccumulator.Entrypoint.Fix.NewOrderSingleConsumer: ExecutionReport not sent: the FIX session is not logged on.");
        Assert.Equal(1, await orderAccumulatorDatabase.CountStoredOrdersAsync("sem-sessao"));
        Assert.Equal(20.00m, await orderAccumulatorDatabase.ReadExposureOfSymbolAsync("VIIA4"));
    }

    [Fact]
    public void Default_acceptor_port_in_appsettings_is_the_contract_9876()
    {
        var orderAccumulatorAppsettings = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
            .Build();

        Assert.Equal(9876, orderAccumulatorAppsettings.GetValue<int>("Fix:AcceptorPort"));
    }

    [Fact]
    public async Task Stopping_and_disposing_the_acceptor_in_any_order_frees_the_fix_port()
    {
        var fixAcceptorPort = OrderAccumulatorFixTestHost.FindFreeFixAcceptorTcpPort();
        var fixAcceptorService = new FixAcceptorWorker(
            new NewOrderSingleConsumer(
                new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
                new ApplicationLogger<NewOrderSingleConsumer>(NullLogger<NewOrderSingleConsumer>.Instance)),
            BuildFixAcceptorConfiguration(("Fix:AcceptorPort", fixAcceptorPort.ToString()), ("Fix:AcceptorBindHost", OrderAccumulatorFixTestHost.FixAcceptorLoopbackBindHost)),
            new FixSessionLogFactory(new ApplicationLogger<FixSessionLog>(NullLogger<FixSessionLog>.Instance)));
        await fixAcceptorService.StartAsync(CancellationToken.None);

        // O host pode descartar antes, depois ou junto com a parada: nenhuma ordem pode lançar exceção.
        await Task.WhenAll(Task.Run(fixAcceptorService.Dispose), fixAcceptorService.StopAsync(CancellationToken.None));
        await fixAcceptorService.StopAsync(CancellationToken.None);
        fixAcceptorService.Dispose();

        Assert.DoesNotContain(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners(),
            tcpListenerEndpoint => tcpListenerEndpoint.Port == fixAcceptorPort);
    }

    [Fact]
    public void Missing_acceptor_port_stops_the_startup_with_a_clear_message()
    {
        var missingAcceptorPortError = Assert.Throws<InvalidOperationException>(() => FixAcceptorWorker.LoadFixAcceptorSessionSettings(BuildFixAcceptorConfiguration()));

        Assert.Equal("Defina a porta do acceptor FIX em Fix__AcceptorPort.", missingAcceptorPortError.Message);
    }

    // GET /api/exposures answers a DataMessage; the exposure body is its "data".
    private static ExposuresResponse ReadExposuresData(JsonElement exposuresDataMessage) =>
        exposuresDataMessage.GetProperty("data").Deserialize<ExposuresResponse>(JsonSerializerOptions.Web)!;

    // As tags da tabela do ExecutionReport no contrato, na ordem dele; tag ausente sai vazia.
    private static string FormatExecutionReportContractTags(ExecutionReport orderExecutionReport) =>
        $"35={orderExecutionReport.Header.GetString(Tags.MsgType)}|" + string.Join('|',
            new[] { Tags.OrderID, Tags.ExecID, Tags.ExecType, Tags.OrdStatus, Tags.ClOrdID, Tags.Symbol, Tags.Side,
                Tags.LeavesQty, Tags.CumQty, Tags.AvgPx, Tags.Text }
            .Select(executionReportTag => $"{executionReportTag}={(orderExecutionReport.IsSetField(executionReportTag) ? orderExecutionReport.GetString(executionReportTag) : "")}"));

    private static IConfiguration BuildFixAcceptorConfiguration(params (string SettingKey, string SettingValue)[] fixAcceptorSettingEntries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(fixAcceptorSettingEntries.Select(fixAcceptorSettingEntry => KeyValuePair.Create(fixAcceptorSettingEntry.SettingKey, (string?)fixAcceptorSettingEntry.SettingValue)))
            .Build();

    private async Task<(string OrderId, string ExecId)> ReadStoredOrderExecutionIdsAsync(string clOrdId)
    {
        await using var orderDatabaseConnection = await orderAccumulatorDatabase.OrderDatabaseDataSource.OpenConnectionAsync();
        return await orderDatabaseConnection.QuerySingleAsync<(string, string)>(
            "SELECT order_id, exec_id FROM orders WHERE cl_ord_id = @ClOrdId", new { ClOrdId = clOrdId });
    }

    // Repository that plays the database being down for one ClOrdID and hands the rest to the real one.
    private sealed class OrderRepositoryFailingForClOrdId(string failingClOrdId, IOrderRepository postgresOrderRepository) : IOrderRepository
    {
        public Task<Order?> FindOrderByClOrdIdAsync(string clOrdId, CancellationToken cancellationToken = default) =>
            clOrdId == failingClOrdId
                ? throw new NpgsqlException("banco fora do ar (simulado no teste)")
                : postgresOrderRepository.FindOrderByClOrdIdAsync(clOrdId, cancellationToken);

        public Task<bool> TryAddOrderAsync(Order answeredOrder, CancellationToken cancellationToken = default) =>
            postgresOrderRepository.TryAddOrderAsync(answeredOrder, cancellationToken);

        public Task DeleteAllOrdersAsync(CancellationToken cancellationToken = default) =>
            postgresOrderRepository.DeleteAllOrdersAsync(cancellationToken);
    }
}
