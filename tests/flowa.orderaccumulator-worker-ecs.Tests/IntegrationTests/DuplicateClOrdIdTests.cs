using System.Collections.Concurrent;
using Dapper;
using Flowa.Commons.Responses;
using Flowa.OrderAccumulator.Application.Orders.Interfaces;
using Flowa.OrderAccumulator.Application.Orders.UseCases;
using Flowa.OrderAccumulator.Infrastructure.Exposures.Adapters;
using QuickFix.Fields;
using QuickFix.FIX44;

namespace Flowa.OrderAccumulator.Tests;

[Collection(OrderAccumulatorPostgresCollection.Name)]
public sealed class DuplicateClOrdIdTests(OrderAccumulatorPostgresFixture orderAccumulatorDatabase) : IAsyncLifetime
{
    private const string OriginalClOrdId = "original-ca19";
    private const string DuplicateClOrdIdRejectionText = "Ordem rejeitada: o ClOrdID original-ca19 já foi usado com outros dados.";
    private const string DuplicateClOrdIdErrorCode = "duplicate-cl-ord-id";

    private const string CountTransactionsWaitingOnExposureRowSql = """
        SELECT count(*)
        FROM pg_stat_activity
        WHERE datname = current_database()
          AND wait_event_type = 'Lock'
          AND query LIKE '%UPDATE exposures%'
        """;

    public Task InitializeAsync() => orderAccumulatorDatabase.ResetOrdersAndExposuresAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    public static TheoryData<string, char, decimal, decimal> OrdersReusingTheClOrdIdWithOneOtherField => new()
    {
        { "VALE3", '1', 100, 10.50m },
        { "PETR4", '2', 100, 10.50m },
        { "PETR4", '1', 101, 10.50m },
        { "PETR4", '1', 100, 10.51m }
    };

    [Theory]
    [MemberData(nameof(OrdersReusingTheClOrdIdWithOneOtherField))]
    public async Task Order_reusing_an_accepted_cl_ord_id_with_other_fields_is_rejected_as_duplicate_and_the_original_stays(
        string duplicateOrderSymbol, char duplicateOrderSide, decimal duplicateOrderQuantity, decimal duplicateOrderPrice)
    {
        // Arrange
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();
        using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort);
        var originalExecutionReport = await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder(OriginalClOrdId, "PETR4", '1', 100, 10.50m));
        var storedOriginalOrderBefore = await ReadStoredOrderAsync(OriginalClOrdId);

        // Act
        var duplicateExecutionReport = await fixTestInitiator.SendExpectingExecutionReportAsync(
            FixTestInitiator.NewOrder(OriginalClOrdId, duplicateOrderSymbol, duplicateOrderSide, duplicateOrderQuantity, duplicateOrderPrice));

        // Assert
        Assert.Equal(ExecType.NEW, originalExecutionReport.ExecType.Value);
        AssertDuplicateOrderExecutionReport(duplicateExecutionReport, duplicateOrderSymbol, duplicateOrderSide);
        Assert.NotEqual(originalExecutionReport.OrderID.Value, duplicateExecutionReport.OrderID.Value);
        Assert.NotEqual(originalExecutionReport.ExecID.Value, duplicateExecutionReport.ExecID.Value);
        Assert.Equal(storedOriginalOrderBefore, await ReadStoredOrderAsync(OriginalClOrdId));
        Assert.Equal(("PETR4", "1", 100m, 10.50m, true, (string?)null), ReadOrderFields(storedOriginalOrderBefore));
        Assert.Equal(1, await orderAccumulatorDatabase.CountStoredOrdersAsync());
        Assert.Equal(1_050.00m, await orderAccumulatorDatabase.ReadExposureOfSymbolAsync("PETR4"));
        Assert.Equal(0m, await orderAccumulatorDatabase.ReadExposureOfSymbolAsync("VALE3"));
        var consumerLogLines = orderAccumulatorTestApp.CapturedOrderAccumulatorLogs.CapturedLogLines
            .Where(capturedLogLine => capturedLogLine.Contains(" Flowa.OrderAccumulator.Entrypoint.Fix.NewOrderSingleConsumer: "))
            .ToList();
        Assert.Equal(["Warning Flowa.OrderAccumulator.Entrypoint.Fix.NewOrderSingleConsumer: Order rejected: ClOrdID already used with other fields."], consumerLogLines);
    }

    [Fact]
    public async Task Duplicate_cl_ord_id_writes_one_warning_with_its_error_code_and_no_error_line()
    {
        // Arrange
        using var stdoutJsonLogCapture = new StdoutJsonLogCapture();
        await using (var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor())
        {
            using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort);
            await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder(OriginalClOrdId, "PETR4", '1', 100, 10.50m));

            // Act
            await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder(OriginalClOrdId, "PETR4", '1', 200, 10.50m));
        }

        // Assert
        var warningOrErrorLines = stdoutJsonLogCapture.JsonLogLines
            .Where(jsonLogLine => jsonLogLine.LogLevel is "Warning" or "Error" or "Critical")
            .ToList();
        var duplicateLogLine = Assert.Single(warningOrErrorLines);
        Assert.Equal(
            ("Warning", "Flowa.OrderAccumulator.Entrypoint.Fix.NewOrderSingleConsumer", "Order rejected: ClOrdID already used with other fields.", DuplicateClOrdIdErrorCode),
            (duplicateLogLine.LogLevel, duplicateLogLine.Category, duplicateLogLine.Message, duplicateLogLine.ReadLogField("ErrorCode")));
        Assert.Null(duplicateLogLine.Exception);
    }

    [Fact]
    public async Task Duplicate_cl_ord_id_is_measured_as_duplicate_and_is_neither_counted_nor_logged_by_the_use_case()
    {
        // Arrange
        var recordingOperationMonitoring = new RecordingOperationMonitoring();
        var recordingOrderMetrics = new RecordingOrderMetrics();
        var recordingUseCaseLogger = new RecordingApplicationLogger<DecideIncomingOrderUseCase>();
        var measuredOrderDecisionRunner = new DecideIncomingOrderTestRunner(
            orderAccumulatorDatabase.OrderDatabaseConnectionSource, new InMemorySymbolExposureAdapter(), recordingOrderMetrics,
            operationMonitoring: recordingOperationMonitoring, orderDecisionLogger: recordingUseCaseLogger);
        await measuredOrderDecisionRunner.DecideIncomingOrderMessageAsync(new(OriginalClOrdId, "PETR4", '1', 100, 10.50m));

        // Act
        var duplicateOrderMessage = await measuredOrderDecisionRunner.DecideIncomingOrderMessageAsync(new(OriginalClOrdId, "PETR4", '2', 100, 10.50m));

        // Assert
        Assert.Equal(DuplicateClOrdIdErrorCode, duplicateOrderMessage.ErrorCode);
        Assert.Equal(
            [(DecideIncomingOrderUseCase.OperationName, "accepted"), (DecideIncomingOrderUseCase.OperationName, "duplicate")],
            recordingOperationMonitoring.RecordedOperations);
        Assert.Equal([("PETR4", '1', true)], recordingOrderMetrics.CountedAnsweredOrders);
        Assert.Equal(["Information Order accepted."], recordingUseCaseLogger.RecordedLogLines);
    }

    [Fact]
    public async Task Order_reusing_a_rejected_cl_ord_id_with_other_fields_is_rejected_as_duplicate_and_the_original_reason_stays()
    {
        // Arrange
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();
        using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort);
        await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder(OriginalClOrdId, "PETR4", '1', 100, 1000m));
        var storedOriginalOrderBefore = await ReadStoredOrderAsync(OriginalClOrdId);

        // Act
        var duplicateExecutionReport = await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder(OriginalClOrdId, "PETR4", '1', 100, 10.00m));

        // Assert
        AssertDuplicateOrderExecutionReport(duplicateExecutionReport, "PETR4", '1');
        Assert.Equal(storedOriginalOrderBefore, await ReadStoredOrderAsync(OriginalClOrdId));
        Assert.Equal(("PETR4", "1", 100m, 1000m, false, "O preço deve ser menor que 1.000,00."), ReadOrderFields(storedOriginalOrderBefore));
        Assert.Equal(0m, await orderAccumulatorDatabase.ReadExposureOfSymbolAsync("PETR4"));
    }

    [Fact]
    public async Task Order_reusing_a_cl_ord_id_with_the_same_fields_gets_the_original_report_without_duplicate_reason()
    {
        // Arrange
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();
        using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort);
        var originalExecutionReport = await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder(OriginalClOrdId, "PETR4", '1', 100, 10.50m));

        // Act
        // 10.5 and 10.50 are the same price: only the value counts, not how FIX wrote it.
        var repeatedExecutionReport = await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder(OriginalClOrdId, "PETR4", '1', 100, 10.5m));

        // Assert
        Assert.Equal(
            (originalExecutionReport.OrderID.Value, originalExecutionReport.ExecID.Value, ExecType.NEW, OrdStatus.NEW),
            (repeatedExecutionReport.OrderID.Value, repeatedExecutionReport.ExecID.Value, repeatedExecutionReport.ExecType.Value, repeatedExecutionReport.OrdStatus.Value));
        Assert.False(repeatedExecutionReport.IsSetOrdRejReason());
        Assert.False(repeatedExecutionReport.IsSetText());
        Assert.Equal(1, await orderAccumulatorDatabase.CountStoredOrdersAsync());
        Assert.Equal(1_050.00m, await orderAccumulatorDatabase.ReadExposureOfSymbolAsync("PETR4"));
    }

    [Fact]
    public async Task Two_simultaneous_orders_with_one_cl_ord_id_and_other_fields_store_one_and_refuse_the_other_as_duplicate()
    {
        // Arrange
        // A separate transaction holds the PETR4 row, so both orders pass the ClOrdID lookup and collide on the insert.
        var orderDecisionRunner = orderAccumulatorDatabase.OrderDecisionRunner;
        await using var exposureRowHolderConnection = await orderAccumulatorDatabase.OrderDatabaseDataSource.OpenConnectionAsync();
        await using var exposureRowHolderTransaction = await exposureRowHolderConnection.BeginTransactionAsync();
        await exposureRowHolderConnection.ExecuteAsync(
            "SELECT exposure FROM exposures WHERE symbol = 'PETR4' FOR UPDATE", transaction: exposureRowHolderTransaction);
        var collidingOrderTasks = new[] { 100m, 200m }
            .Select(collidingOrderQuantity => Task.Run(() => orderDecisionRunner.DecideIncomingOrderMessageAsync(
                new(OriginalClOrdId, "PETR4", '1', collidingOrderQuantity, 10.00m))))
            .ToList();
        Assert.Equal(2, await WaitForTransactionsWaitingOnExposureRowAsync(2));

        // Act
        await exposureRowHolderTransaction.CommitAsync();
        var collidingOrderMessages = await Task.WhenAll(collidingOrderTasks);

        // Assert
        var storedOrderMessage = Assert.Single(collidingOrderMessages, collidingOrderMessage => collidingOrderMessage.Success);
        var duplicateOrderMessage = Assert.Single(collidingOrderMessages, collidingOrderMessage => !collidingOrderMessage.Success);
        Assert.True(storedOrderMessage.Data!.Accepted);
        Assert.Equal(
            (ResultStatus.Conflict, DuplicateClOrdIdErrorCode, DuplicateClOrdIdRejectionText),
            (duplicateOrderMessage.Status, duplicateOrderMessage.ErrorCode, duplicateOrderMessage.Message));
        Assert.Null(duplicateOrderMessage.Data);
        Assert.Null(duplicateOrderMessage.Failure);
        Assert.Equal(1, await orderAccumulatorDatabase.CountStoredOrdersAsync());
        Assert.Equal(storedOrderMessage.Data.Quantity, ReadOrderFields(await ReadStoredOrderAsync(OriginalClOrdId)).Quantity);
        Assert.Equal(storedOrderMessage.Data.Quantity * 10.00m, await orderAccumulatorDatabase.ReadExposureOfSymbolAsync("PETR4"));
    }

    private static void AssertDuplicateOrderExecutionReport(ExecutionReport duplicateExecutionReport, string duplicateOrderSymbol, char duplicateOrderSide) =>
        Assert.Equal(
            (ExecType.REJECTED, OrdStatus.REJECTED, OrdRejReason.DUPLICATE_ORDER, DuplicateClOrdIdRejectionText, OriginalClOrdId,
                duplicateOrderSymbol, duplicateOrderSide, 0m, 0m, 0m),
            (duplicateExecutionReport.ExecType.Value, duplicateExecutionReport.OrdStatus.Value, duplicateExecutionReport.OrdRejReason.Value,
                duplicateExecutionReport.Text.Value, duplicateExecutionReport.ClOrdID.Value, duplicateExecutionReport.Symbol.Value,
                duplicateExecutionReport.Side.Value, duplicateExecutionReport.LeavesQty.Value, duplicateExecutionReport.CumQty.Value,
                duplicateExecutionReport.AvgPx.Value));

    private static (string? Symbol, string Side, decimal Quantity, decimal Price, bool Accepted, string? RejectReason) ReadOrderFields(StoredOrderInTheTable storedOrder) =>
        (storedOrder.Symbol, storedOrder.Side, storedOrder.Quantity, storedOrder.Price, storedOrder.Accepted, storedOrder.RejectReason);

    private async Task<StoredOrderInTheTable> ReadStoredOrderAsync(string clOrdId)
    {
        await using var orderDatabaseConnection = await orderAccumulatorDatabase.OrderDatabaseDataSource.OpenConnectionAsync();
        return await orderDatabaseConnection.QuerySingleAsync<StoredOrderInTheTable>(
            """
            SELECT id AS Id, order_id AS OrderId, exec_id AS ExecId, symbol AS Symbol, side AS Side, quantity AS Quantity, price AS Price,
                   accepted AS Accepted, reject_reason AS RejectReason, received_at AS ReceivedAt
            FROM orders
            WHERE cl_ord_id = @ClOrdId
            """,
            new { ClOrdId = clOrdId });
    }

    private async Task<long> WaitForTransactionsWaitingOnExposureRowAsync(int expectedWaitingTransactions)
    {
        await using var lockWaitMonitorConnection = await orderAccumulatorDatabase.OrderDatabaseDataSource.OpenConnectionAsync();
        var lockWaitDeadline = DateTime.UtcNow.AddSeconds(30);
        long transactionsWaitingOnExposureRow;
        do
        {
            transactionsWaitingOnExposureRow = await lockWaitMonitorConnection.ExecuteScalarAsync<long>(CountTransactionsWaitingOnExposureRowSql);
            if (transactionsWaitingOnExposureRow >= expectedWaitingTransactions)
                return transactionsWaitingOnExposureRow;
            await Task.Delay(50);
        } while (DateTime.UtcNow < lockWaitDeadline);

        return transactionsWaitingOnExposureRow;
    }

    private sealed class RecordingOrderMetrics : IOrderMetricsPort
    {
        private readonly ConcurrentQueue<(string? OrderSymbol, char OrderSide, bool OrderAccepted)> countedAnsweredOrders = new();

        public IReadOnlyList<(string? OrderSymbol, char OrderSide, bool OrderAccepted)> CountedAnsweredOrders => countedAnsweredOrders.ToList();

        public void CountAnsweredOrder(string? orderSymbol, char orderSide, bool orderAccepted) =>
            countedAnsweredOrders.Enqueue((orderSymbol, orderSide, orderAccepted));

        public void SendSymbolExposureGauge(string orderSymbol, decimal symbolExposure)
        {
        }
    }

    private sealed record StoredOrderInTheTable(
        long Id, string OrderId, string ExecId, string? Symbol, string Side, decimal Quantity, decimal Price, bool Accepted, string? RejectReason, DateTime ReceivedAt);
}
