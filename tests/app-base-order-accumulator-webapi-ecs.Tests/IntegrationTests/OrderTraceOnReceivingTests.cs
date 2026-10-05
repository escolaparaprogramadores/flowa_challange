using System.Collections.Concurrent;
using System.Diagnostics;
using Base.OrderAccumulator.Infrastructure.Fix;
using QuickFix.Fields;
using QuickFix.FIX44;

namespace Base.OrderAccumulator.Tests;

// CA-O5 of wave 3: the trace context arrives in tag 5100 and never changes where the order goes.
// The FIX tests of this collection run one at a time, so each one sees only the receiving of its own order.
[Collection(OrderAccumulatorPostgresCollection.Name)]
public sealed class OrderTraceOnReceivingTests(OrderAccumulatorPostgresFixture orderAccumulatorDatabase) : IAsyncLifetime
{
    // Plays the OrderGenerator part: same source name and the same W3C traceparent in tag 5100.
    private static readonly ActivitySource OrderSendingTestSource = new(FixOrderTraceProvider.TraceSourceName);

    public Task InitializeAsync() => orderAccumulatorDatabase.ResetOrdersAndExposuresAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Order_with_traceparent_in_tag_5100_is_accepted_in_the_same_trace_as_the_sending()
    {
        using var capturedOrderTraceSpans = new CapturedOrderTraceSpans();
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();
        using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort);
        using var orderSending = OrderSendingTestSource.StartActivity("fix.envio_da_ordem", ActivityKind.Producer);
        Assert.NotNull(orderSending);
        var orderWithTrace = FixTestInitiator.NewOrder("rastro-com-5100", "PETR4", '1', 100, 10.50m);
        orderWithTrace.SetField(new StringField(FixOrderTraceProvider.TraceParentTag, orderSending.Id!));

        var orderExecutionReport = await fixTestInitiator.SendExpectingExecutionReportAsync(orderWithTrace);

        Assert.Equal(ExecType.NEW, orderExecutionReport.ExecType.Value);
        Assert.Equal(1, await orderAccumulatorDatabase.CountStoredOrdersAsync("rastro-com-5100"));
        var orderReceiving = Assert.Single(capturedOrderTraceSpans.OrderReceivingSpans);
        Assert.Equal(orderSending.TraceId, orderReceiving.TraceId);
        Assert.Equal(orderSending.SpanId, orderReceiving.ParentSpanId);
        Assert.Equal(ActivityKind.Consumer, orderReceiving.Kind);
        // Decision 17: no tag on the span, and never the ClOrdID.
        Assert.Empty(orderReceiving.TagObjects);
    }

    [Fact]
    public async Task Fix_session_log_shows_tag_5100_without_the_trace_id()
    {
        // Reads the real stdout, like FixAcceptorTests: it is what docker compose logs and CloudWatch receive.
        var capturedStdout = new StringWriter();
        var originalStdout = Console.Out;
        Console.SetOut(TextWriter.Synchronized(capturedStdout));
        string sendingTraceId;
        string sendingSpanId;
        try
        {
            using var capturedOrderTraceSpans = new CapturedOrderTraceSpans();
            await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();
            using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort);
            using var orderSending = OrderSendingTestSource.StartActivity("fix.envio_da_ordem", ActivityKind.Producer);
            Assert.NotNull(orderSending);
            sendingTraceId = orderSending.TraceId.ToHexString();
            sendingSpanId = orderSending.SpanId.ToHexString();
            var orderWithTrace = FixTestInitiator.NewOrder("log-sem-trace-id", "PETR4", '1', 100, 10.50m);
            orderWithTrace.SetField(new StringField(FixOrderTraceProvider.TraceParentTag, orderSending.Id!));

            var orderExecutionReport = await fixTestInitiator.SendExpectingExecutionReportAsync(orderWithTrace);

            Assert.Equal(ExecType.NEW, orderExecutionReport.ExecType.Value);
            Assert.Equal(orderSending.TraceId, Assert.Single(capturedOrderTraceSpans.OrderReceivingSpans).TraceId);
        }
        finally
        {
            Console.SetOut(originalStdout);
        }

        var stdoutLines = capturedStdout.ToString().Split(Environment.NewLine);
        Assert.Single(stdoutLines, stdoutLine => stdoutLine.Contains("\u000135=D\u0001")
            && stdoutLine.Contains("\u000111=log-sem-trace-id\u0001") && stdoutLine.Contains("\u00015100=***\u0001"));
        Assert.DoesNotContain(stdoutLines, stdoutLine => stdoutLine.Contains(sendingTraceId));
        Assert.DoesNotContain(stdoutLines, stdoutLine => stdoutLine.Contains(sendingSpanId));
    }

    [Fact]
    public async Task Order_without_tag_5100_is_accepted_and_opens_a_new_trace()
    {
        using var capturedOrderTraceSpans = new CapturedOrderTraceSpans();
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();
        using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort);
        var orderWithoutTrace = FixTestInitiator.NewOrder("rastro-sem-5100", "VALE3", '2', 200, 61.37m);

        var orderExecutionReport = await fixTestInitiator.SendExpectingExecutionReportAsync(orderWithoutTrace);

        Assert.Equal(ExecType.NEW, orderExecutionReport.ExecType.Value);
        Assert.Equal(1, await orderAccumulatorDatabase.CountStoredOrdersAsync("rastro-sem-5100"));
        var orderReceiving = Assert.Single(capturedOrderTraceSpans.OrderReceivingSpans);
        Assert.Equal(default, orderReceiving.ParentSpanId);
        Assert.Null(orderReceiving.ParentId);
        Assert.NotEqual(default, orderReceiving.TraceId);
    }

    [Theory]
    [InlineData("nao-e-um-traceparent")]
    [InlineData("00-00000000000000000000000000000000-0000000000000000-01")]
    [InlineData("00-0af7651916cd43dd8448eb211c80319c-b7ad6b71692033")]
    public async Task Order_with_malformed_tag_5100_is_accepted_and_opens_a_new_trace(string malformedTraceParent)
    {
        using var capturedOrderTraceSpans = new CapturedOrderTraceSpans();
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();
        using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort);
        var orderWithMalformedTrace = FixTestInitiator.NewOrder("rastro-malformado", "VIIA4", '1', 10, 3.21m);
        orderWithMalformedTrace.SetField(new StringField(FixOrderTraceProvider.TraceParentTag, malformedTraceParent));

        var orderExecutionReport = await fixTestInitiator.SendExpectingExecutionReportAsync(orderWithMalformedTrace);

        Assert.Equal(ExecType.NEW, orderExecutionReport.ExecType.Value);
        Assert.Equal(1, await orderAccumulatorDatabase.CountStoredOrdersAsync("rastro-malformado"));
        var orderReceiving = Assert.Single(capturedOrderTraceSpans.OrderReceivingSpans);
        Assert.Equal(default, orderReceiving.ParentSpanId);
        Assert.NotEqual(default, orderReceiving.TraceId);
    }

    [Fact]
    public async Task User_field_outside_the_dictionary_is_still_refused_by_the_fix_session()
    {
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();
        using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort);
        var orderWithUnknownField = FixTestInitiator.NewOrder("campo-5101", "PETR4", '1', 100, 10.50m);
        orderWithUnknownField.SetField(new StringField(5101, "fora-do-dicionario"));

        var fixSessionReject = await fixTestInitiator.SendExpectingSessionRejectAsync(orderWithUnknownField);

        Assert.Equal(5101, fixSessionReject.RefTagID.Value);
        // A tag the dictionary does not even define: QuickFIX refuses it as an invalid tag number (373=0).
        Assert.Equal(SessionRejectReason.INVALID_TAG_NUMBER, fixSessionReject.SessionRejectReason.Value);
        Assert.Equal(0, await orderAccumulatorDatabase.CountStoredOrdersAsync("campo-5101"));
    }

    // Listens only to the order trace source and keeps each receiving span when it starts, before the answer goes out.
    private sealed class CapturedOrderTraceSpans : IDisposable
    {
        private readonly ConcurrentQueue<Activity> _orderReceivingSpans = new();
        private readonly ActivityListener _orderTraceListener;

        public CapturedOrderTraceSpans()
        {
            _orderTraceListener = new ActivityListener
            {
                ShouldListenTo = traceSource => traceSource.Name == FixOrderTraceProvider.TraceSourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStarted = startedSpan =>
                {
                    if (startedSpan.OperationName == "fix.recebimento_da_ordem")
                        _orderReceivingSpans.Enqueue(startedSpan);
                }
            };
            ActivitySource.AddActivityListener(_orderTraceListener);
        }

        public IReadOnlyList<Activity> OrderReceivingSpans => _orderReceivingSpans.ToList();

        public void Dispose() => _orderTraceListener.Dispose();
    }
}
