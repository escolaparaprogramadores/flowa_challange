using System.Diagnostics;
using Base.OrderAccumulator.Commons.Database;
using Base.OrderAccumulator.Domain.Orders.Entities;
using Base.OrderAccumulator.Domain.Orders.Interfaces;
using Base.OrderAccumulator.Entrypoint.Fix;
using Base.OrderAccumulator.Infrastructure.Fix;
using Base.OrderAccumulator.Infrastructure.Orders.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using QuickFix;
using QuickFix.Fields;
using QuickFix.FIX44;

namespace Base.OrderAccumulator.Tests;

// CA-12 and CA-13: each case of an order leaves exactly one log line from the FIX consumer, at the level of the
// table (rule broken = Warning without stack, unexpected = Error with the exception), read from the real stdout
// and carrying the ClOrdID as trace id (CA-8). The orders arrive as the OrderGenerator sends them: the ClOrdID is
// the trace id and tag 5100 carries the traceparent.
[Collection(OrderAccumulatorPostgresCollection.Name)]
public sealed class OrderDecisionLogTests(OrderAccumulatorPostgresFixture orderAccumulatorDatabase) : IAsyncLifetime
{
    private const string NewOrderSingleConsumerCategory = "Base.OrderAccumulator.Entrypoint.Fix.NewOrderSingleConsumer";
    private const string FixSessionLogCategory = "Base.OrderAccumulator.Infrastructure.Fix.FixSessionLog";

    public Task InitializeAsync() => orderAccumulatorDatabase.ResetOrdersAndExposuresAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Order_over_the_exposure_limit_logs_one_warning_with_the_clordid_as_trace_id()
    {
        using var stdoutJsonLogCapture = new StdoutJsonLogCapture();
        using var orderTraceListener = ListenToOrderTraceSource();
        // Each sale is worth 99,998,000.01; the second one would take VIIA4 over 100 million.
        var acceptedSale = NewTracedOrder("VIIA4", '2', 99999, 999.99m);
        var saleOverTheLimit = NewTracedOrder("VIIA4", '2', 99999, 999.99m);
        await using (var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor())
        {
            using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort);
            Assert.Equal(ExecType.NEW, (await fixTestInitiator.SendExpectingExecutionReportAsync(acceptedSale)).ExecType.Value);
            Assert.Equal(ExecType.REJECTED, (await fixTestInitiator.SendExpectingExecutionReportAsync(saleOverTheLimit)).ExecType.Value);
        }

        var rejectionLogLine = AssertSingleWarningOrErrorLineOfTheOrder(stdoutJsonLogCapture, saleOverTheLimit.ClOrdID.Value);
        Assert.Equal(("Warning", "Order rejected: exposure limit exceeded."), (rejectionLogLine.LogLevel, rejectionLogLine.Message));
        Assert.Equal("exposure_limit_exceeded", rejectionLogLine.ReadLogField("ErrorCode"));
        Assert.Null(rejectionLogLine.Exception);
        // The accepted order has no consumer line; its FIX messages in and out already record it.
        Assert.DoesNotContain(stdoutJsonLogCapture.JsonLogLines, jsonLogLine =>
            jsonLogLine.Category == NewOrderSingleConsumerCategory && jsonLogLine.TraceId == acceptedSale.ClOrdID.Value);
        // A rejected order leaves three lines: the FIX message in, the warning and the answer out.
        Assert.Equal(3, stdoutJsonLogCapture.JsonLogLines.Count(jsonLogLine => jsonLogLine.TraceId == saleOverTheLimit.ClOrdID.Value));
    }

    [Fact]
    public async Task Order_with_an_invalid_field_logs_one_warning_with_the_clordid_as_trace_id()
    {
        using var stdoutJsonLogCapture = new StdoutJsonLogCapture();
        using var orderTraceListener = ListenToOrderTraceSource();
        var orderWithUnknownSymbol = NewTracedOrder("ITUB4", '1', 100, 10.50m);
        await using (var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor())
        {
            using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort);
            Assert.Equal(ExecType.REJECTED, (await fixTestInitiator.SendExpectingExecutionReportAsync(orderWithUnknownSymbol)).ExecType.Value);
        }

        var rejectionLogLine = AssertSingleWarningOrErrorLineOfTheOrder(stdoutJsonLogCapture, orderWithUnknownSymbol.ClOrdID.Value);
        Assert.Equal(("Warning", "Order rejected: invalid fields."), (rejectionLogLine.LogLevel, rejectionLogLine.Message));
        Assert.Equal("invalid_order_fields", rejectionLogLine.ReadLogField("ErrorCode"));
        Assert.Null(rejectionLogLine.Exception);
    }

    [Fact]
    public async Task Repeated_order_logs_one_information_line_and_no_second_rejection()
    {
        using var stdoutJsonLogCapture = new StdoutJsonLogCapture();
        using var orderTraceListener = ListenToOrderTraceSource();
        var orderWithUnknownSymbol = NewTracedOrder("ITUB4", '1', 100, 10.50m);
        await using (var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor())
        {
            using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort);
            await fixTestInitiator.SendExpectingExecutionReportAsync(orderWithUnknownSymbol);
            await fixTestInitiator.SendExpectingExecutionReportAsync(orderWithUnknownSymbol);
        }

        // Every line of the order that is not a FIX message, whatever class wrote it: the first rejection and the repeat.
        var orderLinesBesidesFixMessages = stdoutJsonLogCapture.JsonLogLines
            .Where(jsonLogLine => jsonLogLine.TraceId == orderWithUnknownSymbol.ClOrdID.Value && jsonLogLine.Category != FixSessionLogCategory)
            .ToList();
        Assert.Equal(2, orderLinesBesidesFixMessages.Count);
        Assert.All(orderLinesBesidesFixMessages, orderLogLine => Assert.Equal(NewOrderSingleConsumerCategory, orderLogLine.Category));
        var rejectionLogLine = AssertSingleWarningOrErrorLineOfTheOrder(stdoutJsonLogCapture, orderWithUnknownSymbol.ClOrdID.Value);
        Assert.Equal("Order rejected: invalid fields.", rejectionLogLine.Message);
        var repeatLogLine = Assert.Single(orderLinesBesidesFixMessages, orderLogLine => orderLogLine.LogLevel == "Information");
        Assert.Equal("Repeated ClOrdID: sending the stored answer back.", repeatLogLine.Message);
        Assert.Null(repeatLogLine.ReadLogField("ErrorCode"));
    }

    [Fact]
    public async Task Execution_report_that_cannot_be_sent_logs_one_warning_with_the_clordid_as_trace_id()
    {
        using var stdoutJsonLogCapture = new StdoutJsonLogCapture();
        using var orderTraceListener = ListenToOrderTraceSource();
        var orderWithoutLoggedOnSession = NewTracedOrder("VIIA4", '1', 10, 2.00m);
        await using (var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor())
        {
            // The acceptor session exists but nobody is logged on: SendToTarget returns false.
            orderAccumulatorTestApp.Services.GetRequiredService<NewOrderSingleConsumer>()
                .OnMessage(orderWithoutLoggedOnSession, new SessionID("FIX.4.4", "ORDERACCUMULATOR", "ORDERGENERATOR"));
        }

        var notSentLogLine = AssertSingleWarningOrErrorLineOfTheOrder(stdoutJsonLogCapture, orderWithoutLoggedOnSession.ClOrdID.Value);
        Assert.Equal(("Warning", "ExecutionReport not sent: the FIX session is not logged on."), (notSentLogLine.LogLevel, notSentLogLine.Message));
        Assert.Equal("execution_report_not_sent", notSentLogLine.ReadLogField("ErrorCode"));
        Assert.Null(notSentLogLine.Exception);
        Assert.Equal(1, await orderAccumulatorDatabase.CountStoredOrdersAsync(orderWithoutLoggedOnSession.ClOrdID.Value));
    }

    [Fact]
    public async Task Unexpected_failure_logs_one_error_with_the_exception_and_the_clordid_as_trace_id()
    {
        using var stdoutJsonLogCapture = new StdoutJsonLogCapture();
        using var orderTraceListener = ListenToOrderTraceSource();
        var orderThatHitsTheDatabaseFailure = NewTracedOrder("PETR4", '1', 10, 1.00m);
        await using (var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString, replaceOrderAccumulatorServices: orderAccumulatorTestServices =>
            orderAccumulatorTestServices.AddScoped<IOrderRepository>(orderOperationServices =>
                new OrderRepositoryFailingForClOrdId(orderThatHitsTheDatabaseFailure.ClOrdID.Value, new OrderRepository(orderOperationServices.GetRequiredService<IDatabase>())))).StartWithFixAcceptor())
        {
            using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort);
            await fixTestInitiator.ExpectNoAnswerAsync(orderThatHitsTheDatabaseFailure, TimeSpan.FromSeconds(2));
        }

        var failureLogLine = AssertSingleWarningOrErrorLineOfTheOrder(stdoutJsonLogCapture, orderThatHitsTheDatabaseFailure.ClOrdID.Value);
        Assert.Equal(("Error", "Order decision failed; no ExecutionReport sent."), (failureLogLine.LogLevel, failureLogLine.Message));
        Assert.Equal("error", failureLogLine.ReadLogField("ErrorCode"));
        Assert.StartsWith("Npgsql.NpgsqlException", failureLogLine.Exception);
        Assert.Contains(OrderRepositoryFailingForClOrdId.SimulatedDatabaseFailure, failureLogLine.Exception);
    }

    // Exactly one log per error counts every Warning or Error line the app wrote, whatever class wrote it and with or
    // without a trace id; that one line has to be the FIX consumer's, carrying the ClOrdID as trace id.
    private static JsonLogLine AssertSingleWarningOrErrorLineOfTheOrder(StdoutJsonLogCapture stdoutJsonLogCapture, string orderClOrdId)
    {
        var warningOrErrorLine = Assert.Single(stdoutJsonLogCapture.JsonLogLines, jsonLogLine => jsonLogLine.LogLevel is "Warning" or "Error");
        Assert.Equal((NewOrderSingleConsumerCategory, orderClOrdId), (warningOrErrorLine.Category, warningOrErrorLine.TraceId));
        return warningOrErrorLine;
    }

    private static NewOrderSingle NewTracedOrder(string symbol, char side, decimal quantity, decimal price)
    {
        var orderTraceId = ActivityTraceId.CreateRandom().ToHexString();
        var tracedOrder = FixTestInitiator.NewOrder(orderTraceId, symbol, side, quantity, price);
        tracedOrder.SetField(new StringField(FixOrderTraceProvider.TraceParentTag, $"00-{orderTraceId}-{ActivitySpanId.CreateRandom().ToHexString()}-01"));
        return tracedOrder;
    }

    // Plays the Datadog tracer: with nobody listening to the order trace source, the OrderAccumulator opens no span.
    private static ActivityListener ListenToOrderTraceSource()
    {
        var orderTraceListener = new ActivityListener
        {
            ShouldListenTo = traceSource => traceSource.Name == FixOrderTraceProvider.TraceSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(orderTraceListener);
        return orderTraceListener;
    }

    private sealed class OrderRepositoryFailingForClOrdId(string failingClOrdId, IOrderRepository postgresOrderRepository) : IOrderRepository
    {
        public const string SimulatedDatabaseFailure = "database down (simulated in the test)";

        public Task<Order?> FindOrderByClOrdIdAsync(string clOrdId, CancellationToken cancellationToken = default) =>
            clOrdId == failingClOrdId
                ? throw new NpgsqlException(SimulatedDatabaseFailure)
                : postgresOrderRepository.FindOrderByClOrdIdAsync(clOrdId, cancellationToken);

        public Task<bool> TryAddOrderAsync(Order answeredOrder, CancellationToken cancellationToken = default) =>
            postgresOrderRepository.TryAddOrderAsync(answeredOrder, cancellationToken);

        public Task DeleteAllOrdersAsync(CancellationToken cancellationToken = default) =>
            postgresOrderRepository.DeleteAllOrdersAsync(cancellationToken);
    }
}
