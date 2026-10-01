using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Http.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
[Collection(PostgresCollection.Name)]
public sealed class FixAcceptorTests(PostgresFixture db, ITestOutputHelper output) : IAsyncLifetime
{
    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Accepted_order_gets_execution_report_new_with_every_contract_tag()
    {
        await using var app = new AccumulatorApp(db.ConnectionString).Start();
        using var testInitiator = await TestInitiator.ConnectAsync(app.FixPort);
        var acceptedOrder = TestInitiator.NewOrder("aceita-ca8", "PETR4", '1', 100, 10.50m);

        var executionReport = await testInitiator.SendAsync(acceptedOrder);

        var storedAnswer = await StoredAnswerAsync("aceita-ca8");
        Assert.Equal(
            $"35=8|37={storedAnswer.OrderId}|17={storedAnswer.ExecId}|150=0|39=0|11=aceita-ca8|55=PETR4|54=1|151=100|14=0|6=0|58=",
            ContractTagsOf(executionReport));
        Assert.Equal(1_050.00m, await db.ExposureOfAsync("PETR4"));
    }

    [Fact]
    public async Task Order_over_the_limit_gets_execution_report_rejected_with_the_reason()
    {
        await using var app = new AccumulatorApp(db.ConnectionString).Start();
        using var testInitiator = await TestInitiator.ConnectAsync(app.FixPort);
        // 2 × 50.000 × 999,99 = 99.999.000; mais 2.000 × 1,00 passaria de 100.000.000.
        await testInitiator.SendAsync(TestInitiator.NewBuyOrder("VALE3", 50_000, 999.99m));
        await testInitiator.SendAsync(TestInitiator.NewBuyOrder("VALE3", 50_000, 999.99m));
        var overLimitOrder = TestInitiator.NewOrder("estoura-ca9-compra", "VALE3", '1', 2_000, 1.00m);

        var executionReport = await testInitiator.SendAsync(overLimitOrder);

        var storedAnswer = await StoredAnswerAsync("estoura-ca9-compra");
        Assert.Equal(
            $"35=8|37={storedAnswer.OrderId}|17={storedAnswer.ExecId}|150=8|39=8|11=estoura-ca9-compra|55=VALE3|54=1|151=0|14=0|6=0" +
            "|58=Ordem rejeitada: a exposição de VALE3 passaria do limite de 100.000.000,00.",
            ContractTagsOf(executionReport));
        Assert.Equal(99_999_000.00m, await db.ExposureOfAsync("VALE3"));
    }

    public static TheoryData<string, char, string, string, string> InvalidOrders => new()
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
    [MemberData(nameof(InvalidOrders))]
    public async Task Invalid_order_sent_straight_by_fix_is_rejected_and_leaves_exposure_untouched(
        string symbol, char side, string quantity, string price, string expectedText)
    {
        await using var app = new AccumulatorApp(db.ConnectionString).Start();
        using var testInitiator = await TestInitiator.ConnectAsync(app.FixPort);
        var invalidOrder = TestInitiator.NewOrder("invalida-ca13", symbol, side,
            decimal.Parse(quantity, CultureInfo.InvariantCulture), decimal.Parse(price, CultureInfo.InvariantCulture));

        var executionReport = await testInitiator.SendAsync(invalidOrder);

        var storedAnswer = await StoredAnswerAsync("invalida-ca13");
        Assert.Equal(
            $"35=8|37={storedAnswer.OrderId}|17={storedAnswer.ExecId}|150=8|39=8|11=invalida-ca13|55={symbol}|54={side}|151=0|14=0|6=0|58={expectedText}",
            ContractTagsOf(executionReport));

        var exposuresResponse = await app.CreateClient().GetFromJsonAsync<ExposuresResponse>("/api/exposures");
        Assert.Equal([0m, 0m, 0m], exposuresResponse!.Exposures.Select(symbolExposure => symbolExposure.Exposure));
    }

    [Fact]
    public async Task Repeated_cl_ord_id_returns_the_original_report_and_counts_once()
    {
        await using var app = new AccumulatorApp(db.ConnectionString).Start();
        using var testInitiator = await TestInitiator.ConnectAsync(app.FixPort);

        var firstReport = await testInitiator.SendAsync(TestInitiator.NewOrder("repetida", "VIIA4", '1', 10, 5.00m));
        var repeatedReport = await testInitiator.SendAsync(TestInitiator.NewOrder("repetida", "VIIA4", '1', 10, 5.00m));

        Assert.Equal(ContractTagsOf(firstReport), ContractTagsOf(repeatedReport));
        Assert.Equal(ExecType.NEW, repeatedReport.ExecType.Value);
        Assert.Equal(50.00m, await db.ExposureOfAsync("VIIA4"));
        Assert.Equal(1, await db.CountOrdersAsync("repetida"));
        Assert.Single(app.Logs.Lines, logLine =>
            logLine == "Information OrderAccumulator.Fix.OrderFixApplication: ClOrdID repetida repetido: devolvendo a resposta original.");
    }

    [Fact]
    public async Task Repeated_rejected_order_returns_the_same_rejection_text()
    {
        await using var app = new AccumulatorApp(db.ConnectionString).Start();
        using var testInitiator = await TestInitiator.ConnectAsync(app.FixPort);

        var firstReport = await testInitiator.SendAsync(TestInitiator.NewOrder("repetida-invalida", "PETR4", '1', 100, 1000m));
        var repeatedReport = await testInitiator.SendAsync(TestInitiator.NewOrder("repetida-invalida", "PETR4", '1', 100, 1000m));

        Assert.Equal(ContractTagsOf(firstReport), ContractTagsOf(repeatedReport));
        Assert.Equal("O preço deve ser menor que 1.000,00.", repeatedReport.Text.Value);
        Assert.Equal(1, await db.CountOrdersAsync("repetida-invalida"));
    }

    [Fact]
    public async Task Restart_keeps_the_exposure_and_a_resent_order_is_not_counted_again()
    {
        ExecutionReport reportBeforeRestart;
        int fixPort;
        await using (var app = new AccumulatorApp(db.ConnectionString).Start())
        using (var testInitiator = await TestInitiator.ConnectAsync(app.FixPort))
        {
            fixPort = app.FixPort;
            reportBeforeRestart = await testInitiator.SendAsync(TestInitiator.NewOrder("antes-do-reinicio", "VALE3", '1', 100, 10.00m));
        }

        // Volta na MESMA porta: só sobe se a parada anterior fechou o acceptor.
        await using var restartedApp = new AccumulatorApp(db.ConnectionString, fixPort).Start();
        var exposuresResponse = await restartedApp.CreateClient().GetFromJsonAsync<ExposuresResponse>("/api/exposures");
        Assert.Equal(1_000.00m, exposuresResponse!.Exposures.Single(symbolExposure => symbolExposure.Symbol == "VALE3").Exposure);

        using var initiatorAfterRestart = await TestInitiator.ConnectAsync(restartedApp.FixPort);
        var resentReport = await initiatorAfterRestart.SendAsync(TestInitiator.NewOrder("antes-do-reinicio", "VALE3", '1', 100, 10.00m));

        Assert.Equal(ContractTagsOf(reportBeforeRestart), ContractTagsOf(resentReport));
        Assert.Equal(1_000.00m, await db.ExposureOfAsync("VALE3"));
        Assert.Equal(1, await db.CountOrdersAsync("antes-do-reinicio"));
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
            await using var app = new AccumulatorApp(db.ConnectionString).Start();
            using var testInitiator = await TestInitiator.ConnectAsync(app.FixPort);
            await testInitiator.SendAsync(TestInitiator.NewOrder("log-ca19", "PETR4", '2', 5, 20.00m));
        }
        finally
        {
            Console.SetOut(originalStdout);
        }

        var stdoutLines = capturedStdout.ToString().Split(Environment.NewLine);
        foreach (var stdoutLine in stdoutLines)
            output.WriteLine(stdoutLine);

        const string sessionLogPrefix = "info: QuickFix.SessionLogs.FIX.4.4-ORDERACCUMULATOR-ORDERGENERATOR";
        Assert.Single(stdoutLines, stdoutLine => stdoutLine.Contains(sessionLogPrefix) && stdoutLine.Contains("\u000135=D\u0001") && stdoutLine.Contains("\u000111=log-ca19\u0001"));
        Assert.Single(stdoutLines, stdoutLine => stdoutLine.Contains(sessionLogPrefix) && stdoutLine.Contains("\u000135=8\u0001") && stdoutLine.Contains("\u000111=log-ca19\u0001"));
        Assert.Contains(stdoutLines, stdoutLine => stdoutLine.EndsWith(sessionLogPrefix + "[0] Session reset: ResetOnLogon"));
        Assert.Contains(stdoutLines, stdoutLine => stdoutLine.EndsWith(sessionLogPrefix + "[0] Session reset: ResetOnDisconnect"));
    }

    [Fact]
    public async Task Database_failure_sends_no_report_logs_the_order_and_keeps_the_session_up()
    {
        await using var app = new AccumulatorApp(db.ConnectionString, replaceServices: testServices =>
            testServices.AddSingleton<IOrderProcessor>(serviceProvider =>
                new ProcessorFailingForClOrdId("falha-banco", new PostgresOrderProcessor(serviceProvider.GetRequiredService<NpgsqlDataSource>())))).Start();
        using var testInitiator = await TestInitiator.ConnectAsync(app.FixPort);

        await testInitiator.ExpectNoAnswerAsync(TestInitiator.NewOrder("falha-banco", "PETR4", '1', 10, 1.00m), TimeSpan.FromSeconds(2));
        var reportAfterFailure = await testInitiator.SendAsync(TestInitiator.NewOrder("depois-da-falha", "PETR4", '1', 10, 1.00m));

        Assert.Equal(ExecType.NEW, reportAfterFailure.ExecType.Value);
        Assert.Single(app.Logs.Lines, logLine =>
            logLine == "Error OrderAccumulator.Fix.OrderFixApplication: Falha ao processar a ordem falha-banco; nenhum ExecutionReport enviado.");
        Assert.Equal(10.00m, await db.ExposureOfAsync("PETR4"));
    }

    [Fact]
    public void Acceptor_session_is_fix44_with_ephemeral_store_reset_on_every_reconnect()
    {
        var acceptorSettings = FixAcceptorService.LoadSessionSettings(ConfigurationWith(("Fix:AcceptorPort", "19876")));

        var sessionId = Assert.Single(acceptorSettings.GetSessions());
        Assert.Equal(new SessionID("FIX.4.4", "ORDERACCUMULATOR", "ORDERGENERATOR"), sessionId);
        var sessionSettings = acceptorSettings.Get(sessionId);
        Assert.Equal("acceptor", sessionSettings.GetString("ConnectionType"));
        Assert.Equal(19876, sessionSettings.GetInt("SocketAcceptPort"));
        Assert.Equal("Y", sessionSettings.GetString("ResetOnLogon"));
        Assert.Equal("Y", sessionSettings.GetString("ResetOnLogout"));
        Assert.Equal("Y", sessionSettings.GetString("ResetOnDisconnect"));
        Assert.Equal("Y", sessionSettings.GetString("UseDataDictionary"));
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "FIX44.xml"), sessionSettings.GetString("DataDictionary"));
        Assert.True(File.Exists(sessionSettings.GetString("DataDictionary")));
        // Sem Fix__AcceptorBindHost o acceptor escuta em todas as interfaces (o compose precisa disso).
        Assert.False(sessionSettings.Has("SocketAcceptHost"));
    }

    [Fact]
    public void Bind_host_from_configuration_limits_the_acceptor_to_that_address()
    {
        var acceptorSettings = FixAcceptorService.LoadSessionSettings(
            ConfigurationWith(("Fix:AcceptorPort", "19876"), ("Fix:AcceptorBindHost", "127.0.0.1")));

        Assert.Equal("127.0.0.1", acceptorSettings.Get(acceptorSettings.GetSessions().Single()).GetString("SocketAcceptHost"));
    }

    [Fact]
    public async Task Test_app_listens_for_fix_only_on_loopback()
    {
        await using var app = new AccumulatorApp(db.ConnectionString).Start();

        var fixListeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
            .Where(tcpListenerEndpoint => tcpListenerEndpoint.Port == app.FixPort)
            .ToList();

        Assert.Equal([new IPEndPoint(IPAddress.Loopback, app.FixPort)], fixListeners);
    }

    [Fact]
    public async Task Order_without_price_gets_business_reject_and_is_not_recorded()
    {
        await using var app = new AccumulatorApp(db.ConnectionString).Start();
        using var testInitiator = await TestInitiator.ConnectAsync(app.FixPort);
        var orderWithoutPrice = TestInitiator.NewOrder("sem-preco", "PETR4", '1', 10, 1.00m);
        orderWithoutPrice.RemoveField(Tags.Price);

        var businessReject = await testInitiator.SendExpectingBusinessRejectAsync(orderWithoutPrice);
        var reportAfterReject = await testInitiator.SendAsync(TestInitiator.NewOrder("depois-sem-preco", "PETR4", '1', 10, 1.00m));

        Assert.Equal("D", businessReject.RefMsgType.Value);
        // O QuickFIX/n 1.14.1 não preenche RefTagID (371); o motivo vai em 380 e 58.
        Assert.Equal("Conditionally Required Field Missing", businessReject.Text.Value);
        Assert.Equal(BusinessRejectReason.CONDITIONALLY_REQUIRED_FIELD_MISSING, businessReject.BusinessRejectReason.Value);
        Assert.Equal(ExecType.NEW, reportAfterReject.ExecType.Value);
        Assert.Equal(0, await db.CountOrdersAsync("sem-preco"));
        Assert.Equal(10.00m, await db.ExposureOfAsync("PETR4"));
    }

    [Fact]
    public async Task Report_that_cannot_be_sent_is_logged_and_the_order_stays_recorded()
    {
        // Sessão do acceptor criada, mas ninguém logado: o SendToTarget devolve false.
        await using var app = new AccumulatorApp(db.ConnectionString).Start();
        var orderFixApplication = app.Services.GetRequiredService<OrderFixApplication>();
        var acceptorSessionId = new SessionID("FIX.4.4", "ORDERACCUMULATOR", "ORDERGENERATOR");

        orderFixApplication.OnMessage(TestInitiator.NewOrder("sem-sessao", "VIIA4", '1', 10, 2.00m), acceptorSessionId);

        Assert.Single(app.Logs.Lines, logLine =>
            logLine == "Warning OrderAccumulator.Fix.OrderFixApplication: ExecutionReport da ordem sem-sessao não foi enviado: a sessão FIX não está logada.");
        Assert.Equal(1, await db.CountOrdersAsync("sem-sessao"));
        Assert.Equal(20.00m, await db.ExposureOfAsync("VIIA4"));
    }

    [Fact]
    public void Default_acceptor_port_in_appsettings_is_the_contract_9876()
    {
        var appsettings = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
            .Build();

        Assert.Equal(9876, appsettings.GetValue<int>("Fix:AcceptorPort"));
    }

    [Fact]
    public void Missing_acceptor_port_stops_the_startup_with_a_clear_message()
    {
        var startupError = Assert.Throws<InvalidOperationException>(() => FixAcceptorService.LoadSessionSettings(ConfigurationWith()));

        Assert.Equal("Defina a porta do acceptor FIX em Fix__AcceptorPort.", startupError.Message);
    }

    // As tags da tabela do ExecutionReport no contrato, na ordem dele; tag ausente sai vazia.
    private static string ContractTagsOf(ExecutionReport executionReport) =>
        $"35={executionReport.Header.GetString(Tags.MsgType)}|" + string.Join('|',
            new[] { Tags.OrderID, Tags.ExecID, Tags.ExecType, Tags.OrdStatus, Tags.ClOrdID, Tags.Symbol, Tags.Side,
                Tags.LeavesQty, Tags.CumQty, Tags.AvgPx, Tags.Text }
            .Select(tag => $"{tag}={(executionReport.IsSetField(tag) ? executionReport.GetString(tag) : "")}"));

    private static IConfiguration ConfigurationWith(params (string Key, string Value)[] settingEntries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settingEntries.Select(settingEntry => KeyValuePair.Create(settingEntry.Key, (string?)settingEntry.Value)))
            .Build();

    private async Task<(string OrderId, string ExecId)> StoredAnswerAsync(string clOrdId)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<(string, string)>(
            "SELECT order_id, exec_id FROM orders WHERE cl_ord_id = @ClOrdId", new { ClOrdId = clOrdId });
    }

    // Processador que simula o banco fora do ar para um ClOrdID e repassa o resto ao de verdade.
    private sealed class ProcessorFailingForClOrdId(string clOrdId, IOrderProcessor realProcessor) : IOrderProcessor
    {
        public Task<OrderOutcome> ProcessAsync(IncomingOrder order, CancellationToken cancellationToken = default) =>
            order.ClOrdId == clOrdId
                ? throw new NpgsqlException("banco fora do ar (simulado no teste)")
                : realProcessor.ProcessAsync(order, cancellationToken);
    }
}
