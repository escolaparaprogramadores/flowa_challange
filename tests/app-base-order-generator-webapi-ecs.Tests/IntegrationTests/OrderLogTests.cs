using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using Base.OrderGenerator.Infrastructure.Fix;
using QuickFix.Fields;

namespace Base.OrderGenerator.Tests;

// CA-7, CA-8, CA-12 and CA-13 on the OrderGenerator side, read from the real stdout: one JSON line per event,
// the order lines carry the ClOrdID as trace id, and each failed call leaves exactly one line at its level.
// Each test builds its own host inside the capture, because the console logger keeps the stdout it found.
public sealed class OrderLogTests
{
    private const string FixOrderClientCategory = "Base.OrderGenerator.Infrastructure.FixOrderClient";
    private const string FixSessionLogCategory = "Base.OrderGenerator.Infrastructure.Fix.FixSessionLog";
    private const string ValidOrderJson = """{"symbol":"PETR4","side":"buy","quantity":100,"price":10.50}""";

    [Fact]
    public async Task Accepted_order_leaves_two_fix_json_lines_with_the_clordid_as_trace_id_and_the_whole_traceparent()
    {
        using var stdoutJsonLogCapture = new StdoutJsonLogCapture();
        var orderSendingSpans = new ConcurrentQueue<Activity>();
        using var orderTraceListener = ListenToOrderSendingSpans(orderSendingSpans);
        using var fixTestAcceptor = new FixTestAcceptor(OrderGeneratorTestHost.FindFreeTcpPort());
        fixTestAcceptor.ResetToAcceptEveryOrder();
        fixTestAcceptor.StartFixTestAcceptor();
        string orderClOrdId;
        await using (var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(fixTestAcceptor.AcceptorPort))
        {
            using var orderGeneratorClient = orderGeneratorFactory.CreateClient();
            await fixTestAcceptor.WaitForFixSessionLogonAsync();

            var orderHttpResponse = await PostOrder(orderGeneratorClient, ValidOrderJson);

            Assert.Equal(HttpStatusCode.OK, orderHttpResponse.StatusCode);
            orderClOrdId = (await OrderApiTests.ReadOrderGeneratorResponseJson(orderHttpResponse)).GetProperty("clOrdId").GetString()!;
        }

        // Decision 21: the order number is the trace id of the sending span.
        var orderSending = Assert.Single(orderSendingSpans);
        Assert.Equal(orderSending.TraceId.ToHexString(), orderClOrdId);
        var orderLogLines = stdoutJsonLogCapture.JsonLogLines.Where(jsonLogLine => jsonLogLine.TraceId == orderClOrdId).ToList();
        Assert.Equal(2, orderLogLines.Count);
        Assert.All(orderLogLines, orderLogLine => Assert.Equal((FixSessionLogCategory, "Information"), (orderLogLine.Category, orderLogLine.LogLevel)));
        var sentOrderLine = Assert.Single(orderLogLines, orderLogLine => orderLogLine.Message == "FIX message sent.");
        Assert.Contains("|35=D|", sentOrderLine.ReadLogField("FixMessage"));
        Assert.Contains($"|11={orderClOrdId}|", sentOrderLine.ReadLogField("FixMessage"));
        // Decision 17 fell: the traceparent of tag 5100 goes whole to the log.
        Assert.Contains($"|5100=00-{orderClOrdId}-{orderSending.SpanId.ToHexString()}-01|", sentOrderLine.ReadLogField("FixMessage"));
        var receivedExecutionReportLine = Assert.Single(orderLogLines, orderLogLine => orderLogLine.Message == "FIX message received.");
        Assert.Contains("|35=8|", receivedExecutionReportLine.ReadLogField("FixMessage"));
        Assert.Contains($"|11={orderClOrdId}|", receivedExecutionReportLine.ReadLogField("FixMessage"));
        Assert.DoesNotContain(stdoutJsonLogCapture.JsonLogLines, jsonLogLine => jsonLogLine.LogLevel is "Debug" or "Trace");
    }

    [Fact]
    public async Task Order_without_a_logged_on_fix_session_logs_one_warning_inside_the_order_span()
    {
        using var stdoutJsonLogCapture = new StdoutJsonLogCapture();
        var orderSendingSpans = new ConcurrentQueue<Activity>();
        using var orderTraceListener = ListenToOrderSendingSpans(orderSendingSpans);
        await using (var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort()))
        {
            using var orderGeneratorClient = orderGeneratorFactory.CreateClient();
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await PostOrder(orderGeneratorClient, ValidOrderJson)).StatusCode);
        }

        var orderSending = Assert.Single(orderSendingSpans);
        var communicationWarning = AssertSingleWarningOrErrorLine(stdoutJsonLogCapture);
        Assert.Equal(FixOrderClientCategory, communicationWarning.Category);
        Assert.Equal(("Warning", "Order not sent: the FIX session is not logged on."), (communicationWarning.LogLevel, communicationWarning.Message));
        Assert.Equal("communication_error", communicationWarning.ReadLogField("ErrorCode"));
        Assert.Equal(orderSending.TraceId.ToHexString(), communicationWarning.TraceId);
        Assert.Null(communicationWarning.Exception);
    }

    [Fact]
    public async Task Order_without_execution_report_in_5_seconds_logs_one_warning_with_the_clordid_as_trace_id()
    {
        using var stdoutJsonLogCapture = new StdoutJsonLogCapture();
        var orderSendingSpans = new ConcurrentQueue<Activity>();
        using var orderTraceListener = ListenToOrderSendingSpans(orderSendingSpans);
        using var silentFixTestAcceptor = new FixTestAcceptor(OrderGeneratorTestHost.FindFreeTcpPort());
        silentFixTestAcceptor.StartFixTestAcceptor();
        await using (var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(silentFixTestAcceptor.AcceptorPort))
        {
            using var orderGeneratorClient = orderGeneratorFactory.CreateClient();
            await silentFixTestAcceptor.WaitForFixSessionLogonAsync();
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await PostOrder(orderGeneratorClient, ValidOrderJson)).StatusCode);
        }

        var unansweredClOrdId = Assert.Single(silentFixTestAcceptor.ReceivedOrders).GetString(Tags.ClOrdID);
        var communicationWarning = AssertSingleWarningOrErrorLine(stdoutJsonLogCapture);
        Assert.Equal(FixOrderClientCategory, communicationWarning.Category);
        Assert.Equal(("Warning", "No ExecutionReport for the order within 5 seconds."), (communicationWarning.LogLevel, communicationWarning.Message));
        Assert.Equal("communication_error", communicationWarning.ReadLogField("ErrorCode"));
        Assert.Equal(unansweredClOrdId, communicationWarning.TraceId);
        Assert.Null(communicationWarning.Exception);
    }

    [Fact]
    public async Task Execution_report_outside_the_contract_logs_one_error_with_the_exception()
    {
        using var stdoutJsonLogCapture = new StdoutJsonLogCapture();
        var orderSendingSpans = new ConcurrentQueue<Activity>();
        using var orderTraceListener = ListenToOrderSendingSpans(orderSendingSpans);
        using var fixTestAcceptor = new FixTestAcceptor(OrderGeneratorTestHost.FindFreeTcpPort());
        fixTestAcceptor.ExecutionReportResponder = receivedOrder => fixTestAcceptor.BuildExecutionReport(receivedOrder, ExecType.FILL, OrdStatus.FILLED, 0);
        fixTestAcceptor.StartFixTestAcceptor();
        await using (var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(fixTestAcceptor.AcceptorPort))
        {
            using var orderGeneratorClient = orderGeneratorFactory.CreateClient();
            await fixTestAcceptor.WaitForFixSessionLogonAsync();
            Assert.Equal(HttpStatusCode.InternalServerError, (await PostOrder(orderGeneratorClient, ValidOrderJson)).StatusCode);
        }

        var answeredClOrdId = Assert.Single(fixTestAcceptor.ReceivedOrders).GetString(Tags.ClOrdID);
        var unexpectedAnswerError = AssertSingleWarningOrErrorLine(stdoutJsonLogCapture);
        Assert.Equal(FixOrderClientCategory, unexpectedAnswerError.Category);
        Assert.Equal(("Error", "Unexpected ExecutionReport for the order."), (unexpectedAnswerError.LogLevel, unexpectedAnswerError.Message));
        Assert.Equal("error", unexpectedAnswerError.ReadLogField("ErrorCode"));
        Assert.Equal(answeredClOrdId, unexpectedAnswerError.TraceId);
        Assert.StartsWith("System.InvalidOperationException: The OrderAccumulator answered with an ExecutionReport that is neither New nor Rejected.", unexpectedAnswerError.Exception);
    }

    // Route is the route template, not the path the caller typed: the path can vary (case), the template cannot.
    [Theory]
    [InlineData("GET", "/api/exposures", "/api/exposures")]
    [InlineData("GET", "/api/orders?page=2", "/api/orders")]
    [InlineData("DELETE", "/api/orders", "/api/orders")]
    [InlineData("GET", "/API/Exposures", "/api/exposures")]
    public async Task Forwarded_call_with_the_order_accumulator_down_logs_one_warning(string forwardedHttpMethod, string requestedPath, string expectedRouteTemplate)
    {
        using var stdoutJsonLogCapture = new StdoutJsonLogCapture();
        var accumulatorBaseUrlWithNobodyListening = $"http://127.0.0.1:{OrderGeneratorTestHost.FindFreeTcpPort()}";
        await using (var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort(), accumulatorBaseUrlWithNobodyListening))
        {
            using var orderGeneratorClient = orderGeneratorFactory.CreateClient();
            var forwardedCallResponse = await orderGeneratorClient.SendAsync(new HttpRequestMessage(new HttpMethod(forwardedHttpMethod), requestedPath));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, forwardedCallResponse.StatusCode);
        }

        var forwardedCallWarning = AssertSingleWarningOrErrorLine(stdoutJsonLogCapture);
        Assert.Equal(("Program", "Warning", "The OrderAccumulator did not answer the forwarded call."),
            (forwardedCallWarning.Category, forwardedCallWarning.LogLevel, forwardedCallWarning.Message));
        Assert.Equal("communication_error", forwardedCallWarning.ReadLogField("ErrorCode"));
        Assert.Equal(forwardedHttpMethod, forwardedCallWarning.ReadLogField("Method"));
        Assert.Equal(expectedRouteTemplate, forwardedCallWarning.ReadLogField("Route"));
        Assert.Null(forwardedCallWarning.Exception);
    }

    // Exactly one log per error counts every Warning or Error line the app wrote, whatever class wrote it. The test
    // host has no wwwroot, so the static files middleware warns about that at startup; that line is not about the call.
    private static JsonLogLine AssertSingleWarningOrErrorLine(StdoutJsonLogCapture stdoutJsonLogCapture) =>
        Assert.Single(stdoutJsonLogCapture.JsonLogLines, jsonLogLine =>
            jsonLogLine.LogLevel is "Warning" or "Error" && jsonLogLine.Category != "Microsoft.AspNetCore.StaticFiles.StaticFileMiddleware");

    private static Task<HttpResponseMessage> PostOrder(HttpClient orderGeneratorClient, string orderJson) =>
        orderGeneratorClient.PostAsync("/api/orders", new StringContent(orderJson, Encoding.UTF8, "application/json"));

    // Plays the Datadog tracer: with nobody listening to the order trace source, no sending span is created.
    private static ActivityListener ListenToOrderSendingSpans(ConcurrentQueue<Activity> orderSendingSpans)
    {
        var orderTraceListener = new ActivityListener
        {
            ShouldListenTo = traceSource => traceSource.Name == FixOrderTraceProvider.TraceSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = startedSpan =>
            {
                if (startedSpan.OperationName == "fix.envio_da_ordem")
                    orderSendingSpans.Enqueue(startedSpan);
            }
        };
        ActivitySource.AddActivityListener(orderTraceListener);
        return orderTraceListener;
    }
}
