using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Base.OrderGenerator.Infrastructure.Fix;
using QuickFix.Fields;

namespace Base.OrderGenerator.Tests;

// CA-7, CA-8, CA-12 and CA-13 on the OrderGenerator side, read from the real stdout: one JSON line per event,
// the order lines carry the ClOrdID as trace id, and each failed call leaves exactly one line at its level.
// Each test builds its own host inside the capture, because the console logger keeps the stdout it found.
public sealed class OrderLogTests
{
    private const string GlobalErrorHandlerCategory = "Base.OrderGenerator.Entrypoint.Errors.GlobalErrorHandler";
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
            orderClOrdId = (await OrderApiTests.ReadOrderDataAsync(orderHttpResponse)).GetProperty("clOrdId").GetString()!;
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

    // CA-6, CA-11 and CA-13: the 503 of an order that has a ClOrdID leaves one Warning, written by the GlobalErrorHandler
    // in the request span (the parent of the order span), and both the log and the answer carry the ClOrdID as trace id.
    [Fact]
    public async Task Order_without_a_logged_on_fix_session_logs_one_warning_and_answers_the_clordid_as_trace_id()
    {
        using var stdoutJsonLogCapture = new StdoutJsonLogCapture();
        var orderSendingSpans = new ConcurrentQueue<Activity>();
        using var orderTraceListener = ListenToOrderSendingSpans(orderSendingSpans);
        JsonElement communicationErrorProblem;
        await using (var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort()))
        {
            using var orderGeneratorClient = orderGeneratorFactory.CreateClient();
            communicationErrorProblem = await OrderApiTests.ReadProblemDetailsAsync(await PostOrder(orderGeneratorClient, ValidOrderJson), HttpStatusCode.ServiceUnavailable);
        }

        var orderClOrdId = Assert.Single(orderSendingSpans).TraceId.ToHexString();
        Assert.Equal(orderClOrdId, communicationErrorProblem.GetProperty("traceId").GetString());
        AssertSingleHttpErrorLine(stdoutJsonLogCapture, "Warning", "Expected error in request.",
            "urn:base-investimentos:problem:fix-session-not-logged-on", "POST", "/api/orders", orderClOrdId);
    }

    [Fact]
    public async Task Order_without_execution_report_in_5_seconds_logs_one_warning_and_answers_the_clordid_as_trace_id()
    {
        using var stdoutJsonLogCapture = new StdoutJsonLogCapture();
        var orderSendingSpans = new ConcurrentQueue<Activity>();
        using var orderTraceListener = ListenToOrderSendingSpans(orderSendingSpans);
        using var silentFixTestAcceptor = new FixTestAcceptor(OrderGeneratorTestHost.FindFreeTcpPort());
        silentFixTestAcceptor.StartFixTestAcceptor();
        JsonElement communicationErrorProblem;
        await using (var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(silentFixTestAcceptor.AcceptorPort))
        {
            using var orderGeneratorClient = orderGeneratorFactory.CreateClient();
            await silentFixTestAcceptor.WaitForFixSessionLogonAsync();
            communicationErrorProblem = await OrderApiTests.ReadProblemDetailsAsync(await PostOrder(orderGeneratorClient, ValidOrderJson), HttpStatusCode.ServiceUnavailable);
        }

        var unansweredClOrdId = Assert.Single(silentFixTestAcceptor.ReceivedOrders).GetString(Tags.ClOrdID);
        Assert.Equal(unansweredClOrdId, communicationErrorProblem.GetProperty("traceId").GetString());
        AssertSingleHttpErrorLine(stdoutJsonLogCapture, "Warning", "Expected error in request.",
            "urn:base-investimentos:problem:execution-report-timeout", "POST", "/api/orders", unansweredClOrdId);
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
        JsonElement unexpectedErrorProblem;
        await using (var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(fixTestAcceptor.AcceptorPort))
        {
            using var orderGeneratorClient = orderGeneratorFactory.CreateClient();
            await fixTestAcceptor.WaitForFixSessionLogonAsync();
            unexpectedErrorProblem = await OrderApiTests.ReadProblemDetailsAsync(await PostOrder(orderGeneratorClient, ValidOrderJson), HttpStatusCode.InternalServerError);
        }

        var answeredClOrdId = Assert.Single(fixTestAcceptor.ReceivedOrders).GetString(Tags.ClOrdID);
        Assert.Equal(answeredClOrdId, unexpectedErrorProblem.GetProperty("traceId").GetString());
        var unexpectedAnswerError = AssertSingleHttpErrorLine(stdoutJsonLogCapture, "Error", "Unexpected application error.",
            "urn:base-investimentos:problem:internal-error", "POST", "/api/orders", answeredClOrdId);
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
        JsonElement unavailableProblem;
        await using (var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort(), accumulatorBaseUrlWithNobodyListening))
        {
            using var orderGeneratorClient = orderGeneratorFactory.CreateClient();
            var forwardedCallResponse = await orderGeneratorClient.SendAsync(new HttpRequestMessage(new HttpMethod(forwardedHttpMethod), requestedPath));
            unavailableProblem = await OrderApiTests.ReadProblemDetailsAsync(forwardedCallResponse, HttpStatusCode.ServiceUnavailable);
        }

        // The support finds the log line by the traceId of the answer.
        AssertSingleHttpErrorLine(stdoutJsonLogCapture, "Warning", "Expected error in request.",
            "urn:base-investimentos:problem:order-accumulator-unavailable", forwardedHttpMethod, expectedRouteTemplate,
            unavailableProblem.GetProperty("traceId").GetString());
    }

    // Exactly one Warning or Error line for the failed call, from the GlobalErrorHandler, with the problem type as
    // ErrorCode, the method and the route template; an expected error carries no exception, an unexpected one does.
    private static JsonLogLine AssertSingleHttpErrorLine(StdoutJsonLogCapture stdoutJsonLogCapture, string expectedLogLevel, string expectedMessage,
        string expectedErrorCode, string expectedHttpMethod, string expectedRouteTemplate, string? expectedTraceId)
    {
        var httpErrorLine = AssertSingleWarningOrErrorLine(stdoutJsonLogCapture);
        Assert.Equal((GlobalErrorHandlerCategory, expectedLogLevel, expectedMessage), (httpErrorLine.Category, httpErrorLine.LogLevel, httpErrorLine.Message));
        Assert.Equal(expectedErrorCode, httpErrorLine.ReadLogField("ErrorCode"));
        Assert.Equal(expectedHttpMethod, httpErrorLine.ReadLogField("Method"));
        Assert.Equal(expectedRouteTemplate, httpErrorLine.ReadLogField("Route"));
        Assert.Matches("^[0-9a-f]{32}$", expectedTraceId);
        Assert.Equal(expectedTraceId, httpErrorLine.TraceId);
        if (expectedLogLevel == "Warning")
            Assert.Null(httpErrorLine.Exception);
        return httpErrorLine;
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
