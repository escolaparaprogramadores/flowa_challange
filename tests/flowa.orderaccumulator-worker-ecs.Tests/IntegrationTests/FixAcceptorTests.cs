using System.Data;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Net;
using Flowa.Commons.Database;
using Flowa.Commons.Logging;
using Flowa.OrderAccumulator.Domain.Orders.Entities;
using Flowa.OrderAccumulator.Domain.Orders.Interfaces;
using Flowa.OrderAccumulator.Entrypoint.BackgroundService;
using Flowa.OrderAccumulator.Entrypoint.Fix;
using Flowa.OrderAccumulator.Infrastructure.Fix;
using Flowa.OrderAccumulator.Infrastructure.Orders.Repositories;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using QuickFix.Fields;
using QuickFix.FIX44;
using QuickFix;
using Xunit.Abstractions;

namespace Flowa.OrderAccumulator.Tests;

// CA-8, CA-9, CA-13, CA-18 and CA-19 of F3: both real QuickFIX/n ends, with the container PostgreSQL.
[Collection(OrderAccumulatorPostgresCollection.Name)]
public sealed class FixAcceptorTests(OrderAccumulatorPostgresFixture orderAccumulatorDatabase, ITestOutputHelper fixLogTestOutput) : IAsyncLifetime
{
    private static readonly TimeSpan DeadlineClockTolerance = TimeSpan.FromMilliseconds(50);

    public Task InitializeAsync() => orderAccumulatorDatabase.ResetOrdersAndExposuresAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Accepted_order_gets_execution_report_new_with_every_contract_tag()
    {
        await using var orderAccumulatorTestApp = await new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptorAsync();
        using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort);
        var acceptedOrder = FixTestInitiator.NewOrder("accepted-ca8", "PETR4", '1', 100, 10.50m);

        var orderExecutionReport = await fixTestInitiator.SendExpectingExecutionReportAsync(acceptedOrder);

        var storedOrderExecutionIds = await ReadStoredOrderExecutionIdsAsync("accepted-ca8");
        Assert.Equal(
            $"35=8|37={storedOrderExecutionIds.OrderId}|17={storedOrderExecutionIds.ExecId}|150=0|39=0|11=accepted-ca8|55=PETR4|54=1|151=100|14=0|6=0|58=",
            FormatExecutionReportContractTags(orderExecutionReport));
        Assert.Equal(1_050.00m, await orderAccumulatorDatabase.ReadExposureOfSymbolAsync("PETR4"));
    }

    [Fact]
    public async Task Order_over_the_limit_gets_execution_report_rejected_with_the_reason()
    {
        await using var orderAccumulatorTestApp = await new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptorAsync();
        using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort);
        // 2 × 50,000 × 999.99 = 99,999,000; another 2,000 × 1.00 would exceed 100,000,000.
        await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewBuyOrder("VALE3", 50_000, 999.99m));
        await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewBuyOrder("VALE3", 50_000, 999.99m));
        var overLimitOrder = FixTestInitiator.NewOrder("over-limit-ca9-buy", "VALE3", '1', 2_000, 1.00m);

        var orderExecutionReport = await fixTestInitiator.SendExpectingExecutionReportAsync(overLimitOrder);

        var storedOrderExecutionIds = await ReadStoredOrderExecutionIdsAsync("over-limit-ca9-buy");
        Assert.Equal(
            $"35=8|37={storedOrderExecutionIds.OrderId}|17={storedOrderExecutionIds.ExecId}|150=8|39=8|11=over-limit-ca9-buy|55=VALE3|54=1|151=0|14=0|6=0" +
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
        await using var orderAccumulatorTestApp = await new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptorAsync();
        using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort);
        var invalidOrder = FixTestInitiator.NewOrder("invalid-ca13", symbol, side,
            decimal.Parse(quantity, CultureInfo.InvariantCulture), decimal.Parse(price, CultureInfo.InvariantCulture));

        var orderExecutionReport = await fixTestInitiator.SendExpectingExecutionReportAsync(invalidOrder);

        var storedOrderExecutionIds = await ReadStoredOrderExecutionIdsAsync("invalid-ca13");
        Assert.Equal(
            $"35=8|37={storedOrderExecutionIds.OrderId}|17={storedOrderExecutionIds.ExecId}|150=8|39=8|11=invalid-ca13|55={symbol}|54={side}|151=0|14=0|6=0|58={expectedRejectionText}",
            FormatExecutionReportContractTags(orderExecutionReport));

        var storedExposures = await orderAccumulatorDatabase.ExposureReader.GetSymbolExposuresAsync();
        Assert.Equal([0m, 0m, 0m], storedExposures.Select(storedExposure => storedExposure.Exposure));
    }

    [Fact]
    public async Task Repeated_cl_ord_id_returns_the_original_report_and_counts_once()
    {
        await using var orderAccumulatorTestApp = await new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptorAsync();
        using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort);

        var firstOrderExecutionReport = await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder("repeated", "VIIA4", '1', 10, 5.00m));
        var repeatedOrderExecutionReport = await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder("repeated", "VIIA4", '1', 10, 5.00m));

        Assert.Equal(FormatExecutionReportContractTags(firstOrderExecutionReport), FormatExecutionReportContractTags(repeatedOrderExecutionReport));
        Assert.Equal(ExecType.NEW, repeatedOrderExecutionReport.ExecType.Value);
        Assert.Equal(50.00m, await orderAccumulatorDatabase.ReadExposureOfSymbolAsync("VIIA4"));
        Assert.Equal(1, await orderAccumulatorDatabase.CountStoredOrdersAsync("repeated"));
        Assert.Single(orderAccumulatorTestApp.CapturedOrderAccumulatorLogs.CapturedLogLines, capturedLogLine => capturedLogLine == "Information Flowa.OrderAccumulator.Entrypoint.Fix.NewOrderSingleConsumer: Repeated ClOrdID: sending the stored answer back.");
    }

    [Fact]
    public async Task Repeated_rejected_order_returns_the_same_rejection_text()
    {
        await using var orderAccumulatorTestApp = await new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptorAsync();
        using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort);

        var firstOrderExecutionReport = await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder("repeated-invalid", "PETR4", '1', 100, 1000m));
        var repeatedOrderExecutionReport = await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder("repeated-invalid", "PETR4", '1', 100, 1000m));

        Assert.Equal(FormatExecutionReportContractTags(firstOrderExecutionReport), FormatExecutionReportContractTags(repeatedOrderExecutionReport));
        Assert.Equal("O preço deve ser menor que 1.000,00.", repeatedOrderExecutionReport.Text.Value);
        Assert.Equal(1, await orderAccumulatorDatabase.CountStoredOrdersAsync("repeated-invalid"));
    }

    [Fact]
    public async Task Restart_keeps_the_exposure_and_a_resent_order_is_not_counted_again()
    {
        ExecutionReport orderExecutionReportBeforeRestart;
        int fixAcceptorPort;
        await using (var orderAccumulatorTestApp = await new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptorAsync())
        using (var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort))
        {
            fixAcceptorPort = orderAccumulatorTestApp.FixAcceptorPort;
            orderExecutionReportBeforeRestart = await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder("before-restart", "VALE3", '1', 100, 10.00m));
        }

        // Comes back on the SAME port: it only starts if the previous stop closed the acceptor.
        await using var restartedOrderAccumulatorTestApp = await new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString, fixAcceptorPort).StartWithFixAcceptorAsync();
        Assert.Equal(1_000.00m, await orderAccumulatorDatabase.ReadExposureOfSymbolAsync("VALE3"));

        using var fixTestInitiatorAfterRestart = await FixTestInitiator.LogOnToAcceptorAsync(restartedOrderAccumulatorTestApp.FixAcceptorPort);
        var resentOrderExecutionReport = await fixTestInitiatorAfterRestart.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder("before-restart", "VALE3", '1', 100, 10.00m));

        Assert.Equal(FormatExecutionReportContractTags(orderExecutionReportBeforeRestart), FormatExecutionReportContractTags(resentOrderExecutionReport));
        Assert.Equal(1_000.00m, await orderAccumulatorDatabase.ReadExposureOfSymbolAsync("VALE3"));
        Assert.Equal(1, await orderAccumulatorDatabase.CountStoredOrdersAsync("before-restart"));
    }

    [Fact]
    public async Task Fix_heartbeats_are_exchanged_but_never_logged()
    {
        // Decision 22. Positive control: with a 1 s interval the acceptor really sends heartbeats during the test.
        using var stdoutJsonLogCapture = new StdoutJsonLogCapture();
        int heartbeatsSentByTheAcceptor;
        await using (var orderAccumulatorTestApp = await new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptorAsync())
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
            await using (var orderAccumulatorTestApp = await new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptorAsync())
            {
                using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort);
                await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder("log-ca19", "PETR4", '2', 5, 20.00m));
            }

            foreach (var stdoutLine in stdoutJsonLogCapture.StdoutLines)
                fixLogTestOutput.WriteLine(stdoutLine);

            const string fixSessionLogCategory = "Flowa.OrderAccumulator.Infrastructure.Fix.FixSessionLog";
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

    [Theory]
    [InlineData("database-failure-on-read", false)]
    [InlineData("database-failure-on-insert", true)]
    public async Task Database_failure_answers_execution_report_rejected_with_reason_and_leaves_exposure_untouched(string failingClOrdId, bool failsAfterTheExposureUpdate)
    {
        // Arrange
        await using var orderAccumulatorTestApp = await new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString, replaceOrderAccumulatorServices: orderAccumulatorTestServices =>
            orderAccumulatorTestServices.AddScoped<IOrderRepository>(orderOperationServices =>
                new OrderRepositoryFailingForClOrdId(failingClOrdId, new OrderRepository(orderOperationServices.GetRequiredService<IDatabase>()), failsAfterTheExposureUpdate))).StartWithFixAcceptorAsync();
        using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort);

        // Act
        var databaseFailureExecutionReport = await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder(failingClOrdId, "PETR4", '1', 10, 1.00m));
        var exposuresStoredAfterDatabaseFailure = await orderAccumulatorDatabase.ExposureReader.GetSymbolExposuresAsync();
        var orderExecutionReportAfterDatabaseFailure = await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder("after-the-failure", "PETR4", '1', 10, 1.00m));

        // Assert
        AssertRejectedOrderGotNewExecutionIds(databaseFailureExecutionReport);
        Assert.Equal(
            $"35=8|37={databaseFailureExecutionReport.OrderID.Value}|17={databaseFailureExecutionReport.ExecID.Value}|150=8|39=8|11={failingClOrdId}|55=PETR4|54=1|151=0|14=0|6=0" +
            "|58=Ordem rejeitada: o OrderAccumulator não conseguiu decidir a ordem agora. Tente de novo.",
            FormatExecutionReportContractTags(databaseFailureExecutionReport));
        Assert.Equal([0m, 0m, 0m], exposuresStoredAfterDatabaseFailure.Select(storedExposure => storedExposure.Exposure));
        Assert.Equal(0, await orderAccumulatorDatabase.CountStoredOrdersAsync(failingClOrdId));
        Assert.Equal(ExecType.NEW, orderExecutionReportAfterDatabaseFailure.ExecType.Value);
        Assert.Single(orderAccumulatorTestApp.CapturedOrderAccumulatorLogs.CapturedLogLines, capturedLogLine => capturedLogLine == "Error Flowa.OrderAccumulator.Entrypoint.Fix.NewOrderSingleConsumer: Order decision failed; ExecutionReport Rejected sent.");
        Assert.Equal(10.00m, await orderAccumulatorDatabase.ReadExposureOfSymbolAsync("PETR4"));
    }

    [Fact]
    public async Task Slow_database_past_the_default_four_seconds_answers_rejected_by_the_deadline_and_the_session_stays_up()
    {
        // Arrange
        using var stdoutJsonLogCapture = new StdoutJsonLogCapture();
        var stalledOrderInsert = new OrderInsertStalledPastTheDeadline("slow-database", TimeSpan.FromSeconds(6));
        await using var orderAccumulatorTestApp = await new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString, replaceOrderAccumulatorServices: orderAccumulatorTestServices =>
            orderAccumulatorTestServices.AddScoped<IOrderRepository>(orderOperationServices =>
                stalledOrderInsert.WrapPostgresOrderRepository(new OrderRepository(orderOperationServices.GetRequiredService<IDatabase>())))).StartWithFixAcceptorAsync();
        using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort, heartbeatIntervalSeconds: 5);

        // Act
        var answerClock = System.Diagnostics.Stopwatch.StartNew();
        var deadlineExecutionReport = await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder("slow-database", "PETR4", '1', 10, 1.00m));
        var deadlineAnswerTime = answerClock.Elapsed;
        await stalledOrderInsert.StalledInsertFinished.WaitAsync(TimeSpan.FromSeconds(10));
        var resentOrderExecutionReport = await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder("slow-database", "PETR4", '1', 20, 1.00m));
        var heartbeatsBeforeTheQuietSession = fixTestInitiator.ReceivedHeartbeatCount;
        var heartbeatClock = System.Diagnostics.Stopwatch.StartNew();
        while (fixTestInitiator.ReceivedHeartbeatCount == heartbeatsBeforeTheQuietSession && heartbeatClock.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(50);
        var timeUntilTheNextHeartbeat = heartbeatClock.Elapsed;
        var ordersStoredAfterTheStall = await ReadStoredOrderOutcomesAsync();

        // Assert
        Assert.InRange(deadlineAnswerTime, TimeSpan.FromSeconds(4) - DeadlineClockTolerance, TimeSpan.FromSeconds(4.5));
        AssertRejectedOrderGotNewExecutionIds(deadlineExecutionReport);
        Assert.Equal(
            $"35=8|37={deadlineExecutionReport.OrderID.Value}|17={deadlineExecutionReport.ExecID.Value}|150=8|39=8|11=slow-database|55=PETR4|54=1|151=0|14=0|6=0" +
            "|58=Ordem rejeitada: o OrderAccumulator não decidiu a ordem em 4 s. Tente de novo.",
            FormatExecutionReportContractTags(deadlineExecutionReport));
        var resentOrderExecutionIds = await ReadStoredOrderExecutionIdsAsync("slow-database");
        Assert.Equal(
            $"35=8|37={resentOrderExecutionIds.OrderId}|17={resentOrderExecutionIds.ExecId}|150=0|39=0|11=slow-database|55=PETR4|54=1|151=20|14=0|6=0|58=",
            FormatExecutionReportContractTags(resentOrderExecutionReport));
        Assert.InRange(timeUntilTheNextHeartbeat, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(7));
        Assert.Equal(20.00m, await orderAccumulatorDatabase.ReadExposureOfSymbolAsync("PETR4"));
        Assert.Equal(("slow-database", true, 20m), Assert.Single(ordersStoredAfterTheStall));
        Assert.Equal(2, stdoutJsonLogCapture.JsonLogLines.Count(jsonLogLine =>
            jsonLogLine.Message == "FIX message sent." && jsonLogLine.ReadLogField("FixMessage")!.Contains("|35=8|") && jsonLogLine.ReadLogField("FixMessage")!.Contains("|11=slow-database|")));
        Assert.DoesNotContain(stdoutJsonLogCapture.JsonLogLines, jsonLogLine => jsonLogLine.ReadLogField("FixMessage")?.Contains("|35=5|") == true);
        var deadlineLogLine = Assert.Single(stdoutJsonLogCapture.JsonLogLines, jsonLogLine => jsonLogLine.LogLevel is "Warning" or "Error");
        Assert.Equal(("Warning", "Order decision passed the deadline; ExecutionReport Rejected sent.", "order_decision_timeout"),
            (deadlineLogLine.LogLevel, deadlineLogLine.Message, deadlineLogLine.ReadLogField("ErrorCode")));
    }

    [Fact]
    public async Task Deadline_set_to_two_seconds_in_configuration_rejects_at_two_seconds()
    {
        // Arrange
        var stalledOrderInsert = new OrderInsertStalledPastTheDeadline("slow-database-2s", TimeSpan.FromSeconds(4));
        await using var orderAccumulatorTestApp = await new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString, replaceOrderAccumulatorServices: orderAccumulatorTestServices =>
        {
            orderAccumulatorTestServices.AddScoped<IOrderRepository>(orderOperationServices =>
                stalledOrderInsert.WrapPostgresOrderRepository(new OrderRepository(orderOperationServices.GetRequiredService<IDatabase>())));
            ReplaceNewOrderSingleConsumerWithTwoSecondDeadline(orderAccumulatorTestServices);
        }).StartWithFixAcceptorAsync();
        using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort);

        // Act
        var answerClock = System.Diagnostics.Stopwatch.StartNew();
        var deadlineExecutionReport = await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder("slow-database-2s", "VALE3", '1', 10, 1.00m));
        var deadlineAnswerTime = answerClock.Elapsed;
        await stalledOrderInsert.StalledInsertFinished.WaitAsync(TimeSpan.FromSeconds(10));
        var resentOrderExecutionReport = await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder("slow-database-2s", "VALE3", '1', 20, 1.00m));

        // Assert
        Assert.InRange(deadlineAnswerTime, TimeSpan.FromSeconds(2) - DeadlineClockTolerance, TimeSpan.FromSeconds(2.5));
        Assert.Equal(
            $"35=8|37={deadlineExecutionReport.OrderID.Value}|17={deadlineExecutionReport.ExecID.Value}|150=8|39=8|11=slow-database-2s|55=VALE3|54=1|151=0|14=0|6=0" +
            "|58=Ordem rejeitada: o OrderAccumulator não decidiu a ordem em 2 s. Tente de novo.",
            FormatExecutionReportContractTags(deadlineExecutionReport));
        Assert.Equal(ExecType.NEW, resentOrderExecutionReport.ExecType.Value);
        Assert.Equal(1, await orderAccumulatorDatabase.CountStoredOrdersAsync());
        Assert.Equal(20.00m, await orderAccumulatorDatabase.ReadExposureOfSymbolAsync("VALE3"));
    }

    [Fact]
    public async Task Commit_confirmed_by_the_database_after_the_deadline_keeps_the_mirror_equal_to_the_database_and_logs_one_error()
    {
        // Arrange
        using var stdoutJsonLogCapture = new StdoutJsonLogCapture();
        var lateCommitConfirmation = new CommitConfirmedAfterTheDeadline(TimeSpan.FromSeconds(3));
        await using var orderAccumulatorTestApp = await new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString, replaceOrderAccumulatorServices: orderAccumulatorTestServices =>
        {
            orderAccumulatorTestServices.AddScoped<IUnitOfWork>(orderOperationServices =>
                lateCommitConfirmation.WrapDatabaseUnitOfWork(orderOperationServices.GetRequiredService<DatabaseUnitOfWork>()));
            ReplaceNewOrderSingleConsumerWithTwoSecondDeadline(orderAccumulatorTestServices);
        }).StartWithFixAcceptorAsync();
        using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort);

        // Act
        var deadlineExecutionReport = await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder("late-commit", "VIIA4", '1', 10, 1.00m));
        await lateCommitConfirmation.LateCommitReturned.WaitAsync(TimeSpan.FromSeconds(10));
        var lateAcceptanceLogLine = await WaitForSingleErrorLineAsync(stdoutJsonLogCapture);

        // Assert
        Assert.Equal(ExecType.REJECTED, deadlineExecutionReport.ExecType.Value);
        Assert.Equal("Ordem rejeitada: o OrderAccumulator não decidiu a ordem em 2 s. Tente de novo.", deadlineExecutionReport.Text.Value);
        Assert.False(lateCommitConfirmation.CommitCouldBeCancelled);
        Assert.Equal(("Order answered Rejected at the deadline was accepted later by the database.", "order_accepted_after_the_deadline", "late-commit"),
            (lateAcceptanceLogLine.Message, lateAcceptanceLogLine.ReadLogField("ErrorCode"), lateAcceptanceLogLine.ReadLogField("ClOrdId")));
        Assert.StartsWith("System.InvalidOperationException: The order late-commit was answered Rejected at the deadline and then accepted by the database.", lateAcceptanceLogLine.Exception);
        Assert.Equal(1, await orderAccumulatorDatabase.CountStoredOrdersAsync("late-commit"));
        Assert.Equal(10.00m, await orderAccumulatorDatabase.ReadExposureOfSymbolAsync("VIIA4"));
    }

    [Fact]
    public async Task Database_error_that_arrives_after_the_deadline_is_logged_with_its_exception()
    {
        // Arrange
        using var stdoutJsonLogCapture = new StdoutJsonLogCapture();
        var stalledOrderInsert = new OrderInsertStalledPastTheDeadline("late-database-error", TimeSpan.FromSeconds(3),
            new NpgsqlException("connection broken after the deadline (simulated in the test)"));
        await using var orderAccumulatorTestApp = await new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString, replaceOrderAccumulatorServices: orderAccumulatorTestServices =>
        {
            orderAccumulatorTestServices.AddScoped<IOrderRepository>(orderOperationServices =>
                stalledOrderInsert.WrapPostgresOrderRepository(new OrderRepository(orderOperationServices.GetRequiredService<IDatabase>())));
            ReplaceNewOrderSingleConsumerWithTwoSecondDeadline(orderAccumulatorTestServices);
        }).StartWithFixAcceptorAsync();
        using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort);

        // Act
        var deadlineExecutionReport = await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder("late-database-error", "VIIA4", '1', 10, 1.00m));
        await stalledOrderInsert.StalledInsertFinished.WaitAsync(TimeSpan.FromSeconds(10));
        var lateFailureLogLine = await WaitForSingleErrorLineAsync(stdoutJsonLogCapture);

        // Assert
        Assert.Equal("Ordem rejeitada: o OrderAccumulator não decidiu a ordem em 2 s. Tente de novo.", deadlineExecutionReport.Text.Value);
        Assert.Equal(("Order decision failed after the deadline.", "error"), (lateFailureLogLine.Message, lateFailureLogLine.ReadLogField("ErrorCode")));
        Assert.StartsWith("Npgsql.NpgsqlException (0x80004005): connection broken after the deadline (simulated in the test)", lateFailureLogLine.Exception);
        Assert.Equal(0, await orderAccumulatorDatabase.CountStoredOrdersAsync("late-database-error"));
    }

    [Fact]
    public void Order_decision_deadline_is_four_seconds_without_the_key_and_in_appsettings()
    {
        // Arrange
        var orderAccumulatorAppsettings = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
            .Build();

        // Act
        var orderDecisionTimeoutSecondsWithoutTheKey = NewOrderSingleConsumer.ReadOrderDecisionTimeoutSeconds(BuildFixAcceptorConfiguration());
        var orderDecisionTimeoutSecondsInAppsettings = orderAccumulatorAppsettings["Orders:DecisionTimeoutSeconds"];

        // Assert
        Assert.Equal(4, orderDecisionTimeoutSecondsWithoutTheKey);
        Assert.Equal("4", orderDecisionTimeoutSecondsInAppsettings);
    }

    [Fact]
    public void Order_decision_deadline_follows_the_configured_key()
    {
        // Arrange
        var orderAccumulatorConfiguration = BuildFixAcceptorConfiguration(("Orders:DecisionTimeoutSeconds", "7"));

        // Act
        var configuredOrderDecisionTimeoutSeconds = NewOrderSingleConsumer.ReadOrderDecisionTimeoutSeconds(orderAccumulatorConfiguration);

        // Assert
        Assert.Equal(7, configuredOrderDecisionTimeoutSeconds);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void Order_decision_deadline_of_zero_or_less_is_refused_with_a_clear_message(string configuredDeadlineSeconds)
    {
        // Arrange
        var orderAccumulatorConfiguration = BuildFixAcceptorConfiguration(("Orders:DecisionTimeoutSeconds", configuredDeadlineSeconds));

        // Act
        var invalidDeadlineError = Assert.Throws<InvalidOperationException>(() => NewOrderSingleConsumer.ReadOrderDecisionTimeoutSeconds(orderAccumulatorConfiguration));

        // Assert
        Assert.Equal("Set Orders__DecisionTimeoutSeconds to a number of seconds greater than zero.", invalidDeadlineError.Message);
    }

    [Fact]
    public void Acceptor_session_is_fix44_with_ephemeral_store_reset_on_every_reconnect()
    {
        var loadedFixAcceptorSettings = FixAcceptorBackgroundService.LoadFixAcceptorSessionSettings(BuildFixAcceptorConfiguration(("Fix:AcceptorPort", "19876")));

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
        // Without Fix__AcceptorBindHost the acceptor listens on all interfaces (the compose needs that).
        Assert.False(fixSessionSettings.Has("SocketAcceptHost"));
    }

    [Fact]
    public void Bind_host_from_configuration_limits_the_acceptor_to_that_address()
    {
        var loadedFixAcceptorSettings = FixAcceptorBackgroundService.LoadFixAcceptorSessionSettings(
            BuildFixAcceptorConfiguration(("Fix:AcceptorPort", "19876"), ("Fix:AcceptorBindHost", "127.0.0.1")));

        Assert.Equal("127.0.0.1", loadedFixAcceptorSettings.Get(loadedFixAcceptorSettings.GetSessions().Single()).GetString("SocketAcceptHost"));
    }

    [Fact]
    public async Task Test_app_listens_for_fix_only_on_loopback()
    {
        await using var orderAccumulatorTestApp = await new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptorAsync();

        var fixListeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
            .Where(tcpListenerEndpoint => tcpListenerEndpoint.Port == orderAccumulatorTestApp.FixAcceptorPort)
            .ToList();

        Assert.Equal([new IPEndPoint(IPAddress.Loopback, orderAccumulatorTestApp.FixAcceptorPort)], fixListeners);
    }

    public static TheoryData<int, string, string> OrdersMissingQuantityOrPrice => new()
    {
        { Tags.OrderQty, "no-quantity", "A quantidade deve ser maior que zero." },
        { Tags.Price, "no-price", "O preço deve ser maior que zero." }
    };

    [Theory]
    [MemberData(nameof(OrdersMissingQuantityOrPrice))]
    public async Task Order_without_quantity_or_price_gets_execution_report_rejected_with_reason_instead_of_business_reject(
        int missingOrderTag, string clOrdId, string expectedRejectionText)
    {
        // Arrange
        await using var orderAccumulatorTestApp = await new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptorAsync();
        using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort);
        var orderMissingOneField = FixTestInitiator.NewOrder(clOrdId, "PETR4", '1', 10, 1.00m);
        orderMissingOneField.RemoveField(missingOrderTag);

        // Act
        var missingFieldExecutionReport = await fixTestInitiator.SendExpectingExecutionReportAsync(orderMissingOneField);
        var orderExecutionReportAfterMissingField = await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder($"after-{clOrdId}", "PETR4", '1', 10, 1.00m));

        // Assert
        var storedOrderExecutionIds = await ReadStoredOrderExecutionIdsAsync(clOrdId);
        Assert.Equal(
            $"35=8|37={storedOrderExecutionIds.OrderId}|17={storedOrderExecutionIds.ExecId}|150=8|39=8|11={clOrdId}|55=PETR4|54=1|151=0|14=0|6=0|58={expectedRejectionText}",
            FormatExecutionReportContractTags(missingFieldExecutionReport));
        Assert.Equal(ExecType.NEW, orderExecutionReportAfterMissingField.ExecType.Value);
        Assert.Equal(10.00m, await orderAccumulatorDatabase.ReadExposureOfSymbolAsync("PETR4"));
    }

    public static TheoryData<int, string> OrdersMissingFieldRequiredByTheFixDictionary => new()
    {
        { Tags.ClOrdID, "35=3|371=11|372=D|373=1|58=Required tag missing" },
        { Tags.Symbol, "35=3|371=55|372=D|373=1|58=Required tag missing" },
        { Tags.Side, "35=3|371=54|372=D|373=1|58=Required tag missing" }
    };

    [Theory]
    [MemberData(nameof(OrdersMissingFieldRequiredByTheFixDictionary))]
    public async Task Order_without_clordid_symbol_or_side_gets_the_session_reject_of_the_fix_dictionary(int missingOrderTag, string expectedSessionReject)
    {
        // Arrange
        await using var orderAccumulatorTestApp = await new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptorAsync();
        using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort);
        var orderMissingOneField = FixTestInitiator.NewOrder("missing-required-tag", "PETR4", '1', 10, 1.00m);
        orderMissingOneField.RemoveField(missingOrderTag);

        // Act
        var missingFieldSessionReject = await fixTestInitiator.SendExpectingSessionRejectAsync(orderMissingOneField);
        var orderExecutionReportAfterSessionReject = await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder("after-session-reject", "PETR4", '1', 10, 1.00m));

        // Assert
        Assert.Equal(expectedSessionReject, FormatSessionRejectTags(missingFieldSessionReject));
        Assert.Equal(ExecType.NEW, orderExecutionReportAfterSessionReject.ExecType.Value);
        Assert.Equal(1, await orderAccumulatorDatabase.CountStoredOrdersAsync());
    }

    [Fact]
    public async Task Execution_report_that_cannot_be_sent_is_logged_and_the_order_stays_recorded()
    {
        // Acceptor session created, but nobody logged on: SendToTarget returns false.
        await using var orderAccumulatorTestApp = await new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptorAsync();
        var newOrderSingleConsumer = orderAccumulatorTestApp.Services.GetRequiredService<NewOrderSingleConsumer>();
        var acceptorSessionId = new SessionID("FIX.4.4", "ORDERACCUMULATOR", "ORDERGENERATOR");

        newOrderSingleConsumer.OnMessage(FixTestInitiator.NewOrder("no-session", "VIIA4", '1', 10, 2.00m), acceptorSessionId);

        Assert.Single(orderAccumulatorTestApp.CapturedOrderAccumulatorLogs.CapturedLogLines, capturedLogLine => capturedLogLine == "Warning Flowa.OrderAccumulator.Entrypoint.Fix.NewOrderSingleConsumer: ExecutionReport not sent: the FIX session is not logged on.");
        Assert.Equal(1, await orderAccumulatorDatabase.CountStoredOrdersAsync("no-session"));
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
        var fixAcceptorService = new FixAcceptorBackgroundService(
            new NewOrderSingleConsumer(
                new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
                new ApplicationLogger<NewOrderSingleConsumer>(NullLogger<NewOrderSingleConsumer>.Instance),
                BuildFixAcceptorConfiguration()),
            BuildFixAcceptorConfiguration(("Fix:AcceptorPort", fixAcceptorPort.ToString()), ("Fix:AcceptorBindHost", OrderAccumulatorFixTestHost.FixAcceptorLoopbackBindHost)),
            new FixSessionLogFactory(new ApplicationLogger<FixSessionLog>(NullLogger<FixSessionLog>.Instance)));
        await fixAcceptorService.StartAsync(CancellationToken.None);

        // The host may dispose before, after or together with the stop: no order may throw an exception.
        await Task.WhenAll(Task.Run(fixAcceptorService.Dispose), fixAcceptorService.StopAsync(CancellationToken.None));
        await fixAcceptorService.StopAsync(CancellationToken.None);
        fixAcceptorService.Dispose();

        Assert.DoesNotContain(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners(),
            tcpListenerEndpoint => tcpListenerEndpoint.Port == fixAcceptorPort);
    }

    [Fact]
    public void Missing_acceptor_port_stops_the_startup_with_a_clear_message()
    {
        var missingAcceptorPortError = Assert.Throws<InvalidOperationException>(() => FixAcceptorBackgroundService.LoadFixAcceptorSessionSettings(BuildFixAcceptorConfiguration()));

        Assert.Equal("Set the FIX acceptor port in Fix__AcceptorPort.", missingAcceptorPortError.Message);
    }

    // The tags of the ExecutionReport table in the contract, in its order; a missing tag comes out empty.
    private static string FormatExecutionReportContractTags(ExecutionReport orderExecutionReport) =>
        $"35={orderExecutionReport.Header.GetString(Tags.MsgType)}|" + string.Join('|',
            new[] { Tags.OrderID, Tags.ExecID, Tags.ExecType, Tags.OrdStatus, Tags.ClOrdID, Tags.Symbol, Tags.Side,
                Tags.LeavesQty, Tags.CumQty, Tags.AvgPx, Tags.Text }
            .Select(executionReportTag => $"{executionReportTag}={(orderExecutionReport.IsSetField(executionReportTag) ? orderExecutionReport.GetString(executionReportTag) : "")}"));

    private static string FormatSessionRejectTags(Reject fixSessionReject) =>
        $"35={fixSessionReject.Header.GetString(Tags.MsgType)}|" + string.Join('|',
            new[] { Tags.RefTagID, Tags.RefMsgType, Tags.SessionRejectReason, Tags.Text }
            .Select(sessionRejectTag => $"{sessionRejectTag}={(fixSessionReject.IsSetField(sessionRejectTag) ? fixSessionReject.GetString(sessionRejectTag) : "")}"));

    private static void AssertRejectedOrderGotNewExecutionIds(ExecutionReport rejectedOrderExecutionReport)
    {
        Assert.Matches("^[0-9a-f]{32}$", rejectedOrderExecutionReport.OrderID.Value);
        Assert.Matches("^[0-9a-f]{32}$", rejectedOrderExecutionReport.ExecID.Value);
        Assert.NotEqual(rejectedOrderExecutionReport.OrderID.Value, rejectedOrderExecutionReport.ExecID.Value);
    }

    private static void ReplaceNewOrderSingleConsumerWithTwoSecondDeadline(IServiceCollection orderAccumulatorTestServices) =>
        orderAccumulatorTestServices.AddSingleton(orderAccumulatorServices => new NewOrderSingleConsumer(
            orderAccumulatorServices.GetRequiredService<IServiceScopeFactory>(),
            orderAccumulatorServices.GetRequiredService<IApplicationLogger<NewOrderSingleConsumer>>(),
            new ConfigurationBuilder()
                .AddConfiguration(orderAccumulatorServices.GetRequiredService<IConfiguration>())
                .AddInMemoryCollection([KeyValuePair.Create("Orders:DecisionTimeoutSeconds", (string?)"2")])
                .Build()));

    private static async Task<JsonLogLine> WaitForSingleErrorLineAsync(StdoutJsonLogCapture stdoutJsonLogCapture)
    {
        var errorLineClock = System.Diagnostics.Stopwatch.StartNew();
        while (!stdoutJsonLogCapture.JsonLogLines.Any(jsonLogLine => jsonLogLine.LogLevel == "Error") && errorLineClock.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(50);
        return Assert.Single(stdoutJsonLogCapture.JsonLogLines, jsonLogLine => jsonLogLine.LogLevel == "Error");
    }

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

    private async Task<List<(string ClOrdId, bool Accepted, decimal Quantity)>> ReadStoredOrderOutcomesAsync()
    {
        await using var orderDatabaseConnection = await orderAccumulatorDatabase.OrderDatabaseDataSource.OpenConnectionAsync();
        return (await orderDatabaseConnection.QueryAsync<(string, bool, decimal)>("SELECT cl_ord_id, accepted, quantity FROM orders")).ToList();
    }

    // Repository that plays the database being down for one ClOrdID and hands the rest to the real one.
    private sealed class OrderRepositoryFailingForClOrdId(string failingClOrdId, IOrderRepository postgresOrderRepository, bool failsAfterTheExposureUpdate = false) : IOrderRepository
    {
        public Task<Order?> FindOrderByClOrdIdAsync(string clOrdId, CancellationToken cancellationToken = default) =>
            clOrdId == failingClOrdId && !failsAfterTheExposureUpdate
                ? throw new NpgsqlException("database down (simulated in the test)")
                : postgresOrderRepository.FindOrderByClOrdIdAsync(clOrdId, cancellationToken);

        public Task<bool> TryAddOrderAsync(Order answeredOrder, CancellationToken cancellationToken = default) =>
            answeredOrder.ClOrdId == failingClOrdId && failsAfterTheExposureUpdate
                ? throw new NpgsqlException("database down on the order insert (simulated in the test)")
                : postgresOrderRepository.TryAddOrderAsync(answeredOrder, cancellationToken);
    }

    private sealed class OrderInsertStalledPastTheDeadline(string stalledClOrdId, TimeSpan orderInsertStall, Exception? failureAfterTheStall = null)
    {
        private readonly TaskCompletionSource stalledInsertFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int stallsTaken;

        public Task StalledInsertFinished => stalledInsertFinished.Task;

        private string StalledClOrdId => stalledClOrdId;

        private TimeSpan OrderInsertStall => orderInsertStall;

        private Exception? FailureAfterTheStall => failureAfterTheStall;

        private bool TryTakeTheOnlyStall() => Interlocked.Increment(ref stallsTaken) == 1;

        private void MarkStalledInsertFinished() => stalledInsertFinished.TrySetResult();

        public IOrderRepository WrapPostgresOrderRepository(IOrderRepository postgresOrderRepository) =>
            new OrderRepositoryStallingTheInsert(this, postgresOrderRepository);

        private sealed class OrderRepositoryStallingTheInsert(OrderInsertStalledPastTheDeadline stalledOrderInsert, IOrderRepository postgresOrderRepository) : IOrderRepository
        {
            public Task<Order?> FindOrderByClOrdIdAsync(string clOrdId, CancellationToken cancellationToken = default) =>
                postgresOrderRepository.FindOrderByClOrdIdAsync(clOrdId, cancellationToken);

            public async Task<bool> TryAddOrderAsync(Order answeredOrder, CancellationToken cancellationToken = default)
            {
                var wasOrderInserted = await postgresOrderRepository.TryAddOrderAsync(answeredOrder, cancellationToken);
                if (answeredOrder.ClOrdId != stalledOrderInsert.StalledClOrdId || !stalledOrderInsert.TryTakeTheOnlyStall())
                    return wasOrderInserted;

                await Task.Delay(stalledOrderInsert.OrderInsertStall, CancellationToken.None);
                stalledOrderInsert.MarkStalledInsertFinished();
                if (stalledOrderInsert.FailureAfterTheStall is { } failureAfterTheStall)
                    throw failureAfterTheStall;
                return wasOrderInserted;
            }
        }
    }

    private sealed class CommitConfirmedAfterTheDeadline(TimeSpan commitAnswerDelay)
    {
        private readonly TaskCompletionSource lateCommitReturned = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task LateCommitReturned => lateCommitReturned.Task;

        public bool CommitCouldBeCancelled { get; private set; } = true;

        private TimeSpan CommitAnswerDelay => commitAnswerDelay;

        private void MarkLateCommitReturned(bool commitCouldBeCancelled)
        {
            CommitCouldBeCancelled = commitCouldBeCancelled;
            lateCommitReturned.TrySetResult();
        }

        public IUnitOfWork WrapDatabaseUnitOfWork(DatabaseUnitOfWork databaseUnitOfWork) =>
            new UnitOfWorkAnsweringTheCommitLate(this, databaseUnitOfWork);

        private sealed class UnitOfWorkAnsweringTheCommitLate(CommitConfirmedAfterTheDeadline lateCommitConfirmation, DatabaseUnitOfWork databaseUnitOfWork) : IUnitOfWork
        {
            public Task BeginTransactionAsync(IsolationLevel transactionIsolationLevel, CancellationToken cancellationToken = default) =>
                databaseUnitOfWork.BeginTransactionAsync(transactionIsolationLevel, cancellationToken);

            public async Task CommitTransactionAsync(CancellationToken cancellationToken = default)
            {
                await databaseUnitOfWork.CommitTransactionAsync(CancellationToken.None);
                await Task.Delay(lateCommitConfirmation.CommitAnswerDelay, CancellationToken.None);
                lateCommitConfirmation.MarkLateCommitReturned(cancellationToken.CanBeCanceled);
            }

            public Task RollbackTransactionAsync(CancellationToken cancellationToken = default) =>
                databaseUnitOfWork.RollbackTransactionAsync(cancellationToken);
        }
    }
}
