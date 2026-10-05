using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Http.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using OrderAccumulator.Exposure;
using OrderAccumulator.Fix;
using OrderAccumulator.Persistence;
using QuickFix;
using QuickFix.Fields;
using QuickFix.FIX44;
using Xunit.Abstractions;

namespace OrderAccumulator.Tests;

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

        var exposuresResponse = await orderAccumulatorTestApp.CreateClient().GetFromJsonAsync<ExposuresResponse>("/api/exposures");
        Assert.Equal([0m, 0m, 0m], exposuresResponse!.Exposures.Select(symbolExposure => symbolExposure.Exposure));
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
        Assert.Single(orderAccumulatorTestApp.CapturedOrderAccumulatorLogs.CapturedLogLines, capturedLogLine => capturedLogLine == "Information OrderAccumulator.Fix.OrderFixApplication: ClOrdID repetida repetido: devolvendo a resposta original.");
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
        var exposuresResponse = await restartedOrderAccumulatorTestApp.CreateClient().GetFromJsonAsync<ExposuresResponse>("/api/exposures");
        Assert.Equal(1_000.00m, exposuresResponse!.Exposures.Single(symbolExposure => symbolExposure.Symbol == "VALE3").Exposure);

        using var fixTestInitiatorAfterRestart = await FixTestInitiator.LogOnToAcceptorAsync(restartedOrderAccumulatorTestApp.FixAcceptorPort);
        var resentOrderExecutionReport = await fixTestInitiatorAfterRestart.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder("antes-do-reinicio", "VALE3", '1', 100, 10.00m));

        Assert.Equal(FormatExecutionReportContractTags(orderExecutionReportBeforeRestart), FormatExecutionReportContractTags(resentOrderExecutionReport));
        Assert.Equal(1_000.00m, await orderAccumulatorDatabase.ReadExposureOfSymbolAsync("VALE3"));
        Assert.Equal(1, await orderAccumulatorDatabase.CountStoredOrdersAsync("antes-do-reinicio"));
    }

    [Fact]
    public async Task Fix_messages_in_and_out_are_written_to_stdout()
    {
        // Lê o stdout de verdade: o console do app, com o formato de uma linha do appsettings.json.
        var capturedStdout = new StringWriter();
        var originalStdout = Console.Out;
        Console.SetOut(TextWriter.Synchronized(capturedStdout));
        try
        {
            await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();
            using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort);
            await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder("log-ca19", "PETR4", '2', 5, 20.00m));
        }
        finally
        {
            Console.SetOut(originalStdout);
        }

        var stdoutLines = capturedStdout.ToString().Split(Environment.NewLine);
        foreach (var stdoutLine in stdoutLines)
            fixLogTestOutput.WriteLine(stdoutLine);

        const string sessionLogPrefix = "info: QuickFix.SessionLogs.FIX.4.4-ORDERACCUMULATOR-ORDERGENERATOR";
        Assert.Single(stdoutLines, stdoutLine => stdoutLine.Contains(sessionLogPrefix) && stdoutLine.Contains("\u000135=D\u0001") && stdoutLine.Contains("\u000111=log-ca19\u0001"));
        Assert.Single(stdoutLines, stdoutLine => stdoutLine.Contains(sessionLogPrefix) && stdoutLine.Contains("\u000135=8\u0001") && stdoutLine.Contains("\u000111=log-ca19\u0001"));
        Assert.Contains(stdoutLines, stdoutLine => stdoutLine.EndsWith(sessionLogPrefix + "[0] Session reset: ResetOnLogon"));
        Assert.Contains(stdoutLines, stdoutLine => stdoutLine.EndsWith(sessionLogPrefix + "[0] Session reset: ResetOnDisconnect"));
    }

    [Fact]
    public async Task Database_failure_sends_no_execution_report_logs_the_order_and_keeps_the_fix_session_up()
    {
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString, replaceOrderAccumulatorServices: orderAccumulatorTestServices =>
            orderAccumulatorTestServices.AddSingleton<IOrderProcessor>(orderAccumulatorServiceProvider =>
                new OrderProcessorFailingForClOrdId("falha-banco", new PostgresOrderProcessor(orderAccumulatorServiceProvider.GetRequiredService<NpgsqlDataSource>())))).StartWithFixAcceptor();
        using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort);

        await fixTestInitiator.ExpectNoAnswerAsync(FixTestInitiator.NewOrder("falha-banco", "PETR4", '1', 10, 1.00m), TimeSpan.FromSeconds(2));
        var orderExecutionReportAfterDatabaseFailure = await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder("depois-da-falha", "PETR4", '1', 10, 1.00m));

        Assert.Equal(ExecType.NEW, orderExecutionReportAfterDatabaseFailure.ExecType.Value);
        Assert.Single(orderAccumulatorTestApp.CapturedOrderAccumulatorLogs.CapturedLogLines, capturedLogLine => capturedLogLine == "Error OrderAccumulator.Fix.OrderFixApplication: Falha ao processar a ordem falha-banco; nenhum ExecutionReport enviado.");
        Assert.Equal(10.00m, await orderAccumulatorDatabase.ReadExposureOfSymbolAsync("PETR4"));
    }

    [Fact]
    public void Acceptor_session_is_fix44_with_ephemeral_store_reset_on_every_reconnect()
    {
        var loadedFixAcceptorSettings = FixAcceptorService.LoadFixAcceptorSessionSettings(BuildFixAcceptorConfiguration(("Fix:AcceptorPort", "19876")));

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
        var loadedFixAcceptorSettings = FixAcceptorService.LoadFixAcceptorSessionSettings(
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
        var orderFixApplication = orderAccumulatorTestApp.Services.GetRequiredService<OrderFixApplication>();
        var acceptorSessionId = new SessionID("FIX.4.4", "ORDERACCUMULATOR", "ORDERGENERATOR");

        orderFixApplication.OnMessage(FixTestInitiator.NewOrder("sem-sessao", "VIIA4", '1', 10, 2.00m), acceptorSessionId);

        Assert.Single(orderAccumulatorTestApp.CapturedOrderAccumulatorLogs.CapturedLogLines, capturedLogLine => capturedLogLine == "Warning OrderAccumulator.Fix.OrderFixApplication: ExecutionReport da ordem sem-sessao não foi enviado: a sessão FIX não está logada.");
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
        var fixAcceptorService = new FixAcceptorService(
            new OrderFixApplication(orderAccumulatorDatabase.OrderProcessor, NullLogger<OrderFixApplication>.Instance),
            BuildFixAcceptorConfiguration(("Fix:AcceptorPort", fixAcceptorPort.ToString()), ("Fix:AcceptorBindHost", OrderAccumulatorFixTestHost.FixAcceptorLoopbackBindHost)),
            NullLoggerFactory.Instance);
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
        var missingAcceptorPortError = Assert.Throws<InvalidOperationException>(() => FixAcceptorService.LoadFixAcceptorSessionSettings(BuildFixAcceptorConfiguration()));

        Assert.Equal("Defina a porta do acceptor FIX em Fix__AcceptorPort.", missingAcceptorPortError.Message);
    }

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

    // Processador que simula o banco fora do ar para um ClOrdID e repassa o resto ao de verdade.
    private sealed class OrderProcessorFailingForClOrdId(string failingClOrdId, IOrderProcessor postgresOrderProcessor) : IOrderProcessor
    {
        public Task<OrderOutcome> ProcessIncomingOrderAsync(IncomingOrder incomingOrder, CancellationToken cancellationToken = default) =>
            incomingOrder.ClOrdId == failingClOrdId
                ? throw new NpgsqlException("banco fora do ar (simulado no teste)")
                : postgresOrderProcessor.ProcessIncomingOrderAsync(incomingOrder, cancellationToken);
    }
}
