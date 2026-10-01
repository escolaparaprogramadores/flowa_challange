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
        using var fix = await TestInitiator.ConnectAsync(app.FixPort);
        var order = TestInitiator.Order("aceita-ca8", "PETR4", '1', 100, 10.50m);

        var report = await fix.SendAsync(order);

        var stored = await StoredAnswerAsync("aceita-ca8");
        Assert.Equal("8", report.Header.GetString(Tags.MsgType));
        Assert.Equal(ExecType.NEW, report.ExecType.Value);
        Assert.Equal(OrdStatus.NEW, report.OrdStatus.Value);
        Assert.Equal(stored.OrderId, report.OrderID.Value);
        Assert.Equal(stored.ExecId, report.ExecID.Value);
        Assert.Equal("aceita-ca8", report.ClOrdID.Value);
        Assert.Equal("PETR4", report.Symbol.Value);
        Assert.Equal(Side.BUY, report.Side.Value);
        Assert.Equal(100m, report.LeavesQty.Value);
        Assert.Equal(0m, report.CumQty.Value);
        Assert.Equal(0m, report.AvgPx.Value);
        Assert.False(report.IsSetText());
        Assert.Equal(1_050.00m, await db.ExposureOfAsync("PETR4"));
    }

    [Fact]
    public async Task Order_over_the_limit_gets_execution_report_rejected_with_the_reason()
    {
        await using var app = new AccumulatorApp(db.ConnectionString).Start();
        using var fix = await TestInitiator.ConnectAsync(app.FixPort);
        // 2 × 50.000 × 999,99 = 99.999.000; mais 2.000 × 1,00 passaria de 100.000.000.
        await fix.SendAsync(TestInitiator.Buy("VALE3", 50_000, 999.99m));
        await fix.SendAsync(TestInitiator.Buy("VALE3", 50_000, 999.99m));
        var overLimit = TestInitiator.Order("estoura-ca9-compra", "VALE3", '1', 2_000, 1.00m);

        var report = await fix.SendAsync(overLimit);

        var stored = await StoredAnswerAsync("estoura-ca9-compra");
        Assert.Equal("8", report.Header.GetString(Tags.MsgType));
        Assert.Equal(ExecType.REJECTED, report.ExecType.Value);
        Assert.Equal(OrdStatus.REJECTED, report.OrdStatus.Value);
        Assert.Equal("Ordem rejeitada: a exposição de VALE3 passaria do limite de 100.000.000,00.", report.Text.Value);
        Assert.Equal(stored.OrderId, report.OrderID.Value);
        Assert.Equal(stored.ExecId, report.ExecID.Value);
        Assert.Equal("estoura-ca9-compra", report.ClOrdID.Value);
        Assert.Equal("VALE3", report.Symbol.Value);
        Assert.Equal(Side.BUY, report.Side.Value);
        Assert.Equal(0m, report.LeavesQty.Value);
        Assert.Equal(0m, report.CumQty.Value);
        Assert.Equal(0m, report.AvgPx.Value);
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
        using var fix = await TestInitiator.ConnectAsync(app.FixPort);
        var order = TestInitiator.Order("invalida-ca13", symbol, side,
            decimal.Parse(quantity, CultureInfo.InvariantCulture), decimal.Parse(price, CultureInfo.InvariantCulture));

        var report = await fix.SendAsync(order);

        Assert.Equal(ExecType.REJECTED, report.ExecType.Value);
        Assert.Equal(OrdStatus.REJECTED, report.OrdStatus.Value);
        Assert.Equal(expectedText, report.Text.Value);
        Assert.Equal("invalida-ca13", report.ClOrdID.Value);
        Assert.Equal(symbol, report.Symbol.Value);
        Assert.Equal(side, report.Side.Value);
        Assert.Equal(0m, report.LeavesQty.Value);
        Assert.Equal(0m, report.CumQty.Value);
        Assert.Equal(0m, report.AvgPx.Value);
        var stored = await StoredAnswerAsync("invalida-ca13");
        Assert.Equal(stored.OrderId, report.OrderID.Value);
        Assert.Equal(stored.ExecId, report.ExecID.Value);

        var exposures = await app.CreateClient().GetFromJsonAsync<ExposuresResponse>("/api/exposures");
        Assert.Equal([0m, 0m, 0m], exposures!.Exposures.Select(item => item.Exposure));
    }

    [Fact]
    public async Task Repeated_cl_ord_id_returns_the_original_report_and_counts_once()
    {
        await using var app = new AccumulatorApp(db.ConnectionString).Start();
        using var fix = await TestInitiator.ConnectAsync(app.FixPort);

        var first = await fix.SendAsync(TestInitiator.Order("repetida", "VIIA4", '1', 10, 5.00m));
        var again = await fix.SendAsync(TestInitiator.Order("repetida", "VIIA4", '1', 10, 5.00m));

        AssertSameAnswer(first, again);
        Assert.Equal(ExecType.NEW, again.ExecType.Value);
        Assert.Equal(50.00m, await db.ExposureOfAsync("VIIA4"));
        Assert.Equal(1, await db.CountOrdersAsync("repetida"));
        Assert.Single(app.Logs.Lines, line =>
            line == "Information OrderAccumulator.Fix.OrderFixApplication: ClOrdID repetida repetido: devolvendo a resposta original.");
    }

    [Fact]
    public async Task Repeated_rejected_order_returns_the_same_rejection_text()
    {
        await using var app = new AccumulatorApp(db.ConnectionString).Start();
        using var fix = await TestInitiator.ConnectAsync(app.FixPort);

        var first = await fix.SendAsync(TestInitiator.Order("repetida-invalida", "PETR4", '1', 100, 1000m));
        var again = await fix.SendAsync(TestInitiator.Order("repetida-invalida", "PETR4", '1', 100, 1000m));

        AssertSameAnswer(first, again);
        Assert.Equal("O preço deve ser menor que 1.000,00.", again.Text.Value);
        Assert.Equal(1, await db.CountOrdersAsync("repetida-invalida"));
    }

    [Fact]
    public async Task Restart_keeps_the_exposure_and_a_resent_order_is_not_counted_again()
    {
        ExecutionReport beforeRestart;
        int port;
        await using (var app = new AccumulatorApp(db.ConnectionString).Start())
        using (var fix = await TestInitiator.ConnectAsync(app.FixPort))
        {
            port = app.FixPort;
            beforeRestart = await fix.SendAsync(TestInitiator.Order("antes-do-reinicio", "VALE3", '1', 100, 10.00m));
        }

        // Volta na MESMA porta: só sobe se a parada anterior fechou o acceptor.
        await using var restarted = new AccumulatorApp(db.ConnectionString, port).Start();
        var exposures = await restarted.CreateClient().GetFromJsonAsync<ExposuresResponse>("/api/exposures");
        Assert.Equal(1_000.00m, exposures!.Exposures.Single(item => item.Symbol == "VALE3").Exposure);

        using var fixAgain = await TestInitiator.ConnectAsync(restarted.FixPort);
        var resent = await fixAgain.SendAsync(TestInitiator.Order("antes-do-reinicio", "VALE3", '1', 100, 10.00m));

        AssertSameAnswer(beforeRestart, resent);
        Assert.Equal(1_000.00m, await db.ExposureOfAsync("VALE3"));
        Assert.Equal(1, await db.CountOrdersAsync("antes-do-reinicio"));
    }

    [Fact]
    public async Task Fix_messages_in_and_out_are_written_to_stdout()
    {
        // Lê o stdout de verdade: o console do app, com o formato de uma linha do appsettings.json.
        var stdout = new StringWriter();
        var original = Console.Out;
        Console.SetOut(TextWriter.Synchronized(stdout));
        try
        {
            await using var app = new AccumulatorApp(db.ConnectionString).Start();
            using var fix = await TestInitiator.ConnectAsync(app.FixPort);
            await fix.SendAsync(TestInitiator.Order("log-ca19", "PETR4", '2', 5, 20.00m));
        }
        finally
        {
            Console.SetOut(original);
        }

        var lines = stdout.ToString().Split(Environment.NewLine);
        foreach (var line in lines)
            output.WriteLine(line);

        const string session = "info: QuickFix.SessionLogs.FIX.4.4-ORDERACCUMULATOR-ORDERGENERATOR";
        Assert.Single(lines, line => line.Contains(session) && line.Contains("\u000135=D\u0001") && line.Contains("\u000111=log-ca19\u0001"));
        Assert.Single(lines, line => line.Contains(session) && line.Contains("\u000135=8\u0001") && line.Contains("\u000111=log-ca19\u0001"));
        Assert.Contains(lines, line => line.EndsWith(session + "[0] Session reset: ResetOnLogon"));
        Assert.Contains(lines, line => line.EndsWith(session + "[0] Session reset: ResetOnDisconnect"));
    }

    [Fact]
    public async Task Database_failure_sends_no_report_logs_the_order_and_keeps_the_session_up()
    {
        await using var app = new AccumulatorApp(db.ConnectionString, replaceServices: services =>
            services.AddSingleton<IOrderProcessor>(provider =>
                new FailsFor("falha-banco", new PostgresOrderProcessor(provider.GetRequiredService<NpgsqlDataSource>())))).Start();
        using var fix = await TestInitiator.ConnectAsync(app.FixPort);

        await fix.ExpectNoAnswerAsync(TestInitiator.Order("falha-banco", "PETR4", '1', 10, 1.00m), TimeSpan.FromSeconds(2));
        var next = await fix.SendAsync(TestInitiator.Order("depois-da-falha", "PETR4", '1', 10, 1.00m));

        Assert.Equal(ExecType.NEW, next.ExecType.Value);
        Assert.Single(app.Logs.Lines, line =>
            line == "Error OrderAccumulator.Fix.OrderFixApplication: Falha ao processar a ordem falha-banco; nenhum ExecutionReport enviado.");
        Assert.Equal(0, await db.CountOrdersAsync("falha-banco"));
        Assert.Equal(10.00m, await db.ExposureOfAsync("PETR4"));
    }

    [Fact]
    public void Acceptor_session_is_fix44_with_ephemeral_store_reset_on_every_reconnect()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Fix:AcceptorPort"] = "19876" })
            .Build();

        var settings = FixAcceptorService.LoadSettings(configuration);

        var session = Assert.Single(settings.GetSessions());
        Assert.Equal(new SessionID("FIX.4.4", "ORDERACCUMULATOR", "ORDERGENERATOR"), session);
        var values = settings.Get(session);
        Assert.Equal("acceptor", values.GetString("ConnectionType"));
        Assert.Equal(19876, values.GetInt("SocketAcceptPort"));
        Assert.Equal("Y", values.GetString("ResetOnLogon"));
        Assert.Equal("Y", values.GetString("ResetOnLogout"));
        Assert.Equal("Y", values.GetString("ResetOnDisconnect"));
        Assert.Equal("Y", values.GetString("UseDataDictionary"));
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "FIX44.xml"), values.GetString("DataDictionary"));
        Assert.True(File.Exists(values.GetString("DataDictionary")));
        // Sem Fix__AcceptorBindHost o acceptor escuta em todas as interfaces (o compose precisa disso).
        Assert.False(values.Has("SocketAcceptHost"));
    }

    [Fact]
    public void Bind_host_from_configuration_limits_the_acceptor_to_that_address()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Fix:AcceptorPort"] = "19876",
                ["Fix:AcceptorBindHost"] = "127.0.0.1"
            })
            .Build();

        var settings = FixAcceptorService.LoadSettings(configuration);

        Assert.Equal("127.0.0.1", settings.Get(settings.GetSessions().Single()).GetString("SocketAcceptHost"));
    }

    [Fact]
    public async Task Test_app_listens_for_fix_only_on_loopback()
    {
        await using var app = new AccumulatorApp(db.ConnectionString).Start();

        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
            .Where(endpoint => endpoint.Port == app.FixPort)
            .ToList();

        Assert.Equal([new IPEndPoint(IPAddress.Loopback, app.FixPort)], listeners);
    }

    [Fact]
    public void Missing_acceptor_port_stops_the_startup_with_a_clear_message()
    {
        var configuration = new ConfigurationBuilder().Build();

        var error = Assert.Throws<InvalidOperationException>(() => FixAcceptorService.LoadSettings(configuration));

        Assert.Equal("Defina a porta do acceptor FIX em Fix__AcceptorPort.", error.Message);
    }

    private static void AssertSameAnswer(ExecutionReport original, ExecutionReport repeated)
    {
        Assert.Equal(original.OrderID.Value, repeated.OrderID.Value);
        Assert.Equal(original.ExecID.Value, repeated.ExecID.Value);
        Assert.Equal(original.ExecType.Value, repeated.ExecType.Value);
        Assert.Equal(original.OrdStatus.Value, repeated.OrdStatus.Value);
        Assert.Equal(original.IsSetText() ? original.Text.Value : null, repeated.IsSetText() ? repeated.Text.Value : null);
        Assert.Equal(original.LeavesQty.Value, repeated.LeavesQty.Value);
        Assert.Equal(original.ClOrdID.Value, repeated.ClOrdID.Value);
        Assert.Equal(original.Symbol.Value, repeated.Symbol.Value);
        Assert.Equal(original.Side.Value, repeated.Side.Value);
        Assert.Equal(original.CumQty.Value, repeated.CumQty.Value);
        Assert.Equal(original.AvgPx.Value, repeated.AvgPx.Value);
    }

    private async Task<(string OrderId, string ExecId)> StoredAnswerAsync(string clOrdId)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<(string, string)>(
            "SELECT order_id, exec_id FROM orders WHERE cl_ord_id = @ClOrdId", new { ClOrdId = clOrdId });
    }

    // Processador que simula o banco fora do ar para um ClOrdID e repassa o resto ao de verdade.
    private sealed class FailsFor(string clOrdId, IOrderProcessor real) : IOrderProcessor
    {
        public Task<OrderOutcome> ProcessAsync(IncomingOrder order, CancellationToken cancellationToken = default) =>
            order.ClOrdId == clOrdId
                ? throw new NpgsqlException("banco fora do ar (simulado no teste)")
                : real.ProcessAsync(order, cancellationToken);
    }
}
