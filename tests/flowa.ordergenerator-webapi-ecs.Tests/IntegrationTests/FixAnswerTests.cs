using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Flowa.OrderGenerator.Infrastructure.Fix;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using QuickFix.Fields;
using Xunit.Abstractions;

namespace Flowa.OrderGenerator.Tests;

// Reject and BusinessMessageReject answer at once, a dropped session ends every wait at once, the deadline comes
// from configuration and a late ExecutionReport leaves one warning.
public sealed class FixAnswerTests
{
    private const string ValidOrderJson = """{"symbol":"PETR4","side":"buy","quantity":100,"price":10.50}""";
    private const string FixOrderRejectedType = "urn:base-investimentos:problem:fix-order-rejected";

    private readonly ITestOutputHelper _measuredTimeOutput;

    public FixAnswerTests(ITestOutputHelper measuredTimeOutput)
    {
        _measuredTimeOutput = measuredTimeOutput ?? throw new ArgumentNullException(nameof(measuredTimeOutput));
    }

    [Fact]
    public async Task Session_reject_of_the_order_answers_422_with_its_tag_58_in_under_1_second()
    {
        // Arrange
        using var fixTestAcceptor = StartFixTestAcceptor();
        fixTestAcceptor.ExecutionReportResponder = receivedOrder =>
            fixTestAcceptor.BuildSessionReject(receivedOrder, "Tag 44 com formato inválido.", SessionRejectReason.INCORRECT_DATA_FORMAT_FOR_VALUE);
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(fixTestAcceptor.AcceptorPort);
        using var orderGeneratorClient = await CreateLoggedOnClientAsync(orderGeneratorFactory, fixTestAcceptor);

        // Act
        var apiResponseClock = Stopwatch.StartNew();
        var orderHttpResponse = await PostValidOrder(orderGeneratorClient);
        apiResponseClock.Stop();
        _measuredTimeOutput.WriteLine($"measured: {apiResponseClock.Elapsed.TotalMilliseconds:F0} ms");

        // Assert
        var fixRejectProblem = await AssertFixOrderRejectedProblem(orderHttpResponse, "Tag 44 com formato inválido.");
        Assert.True(apiResponseClock.Elapsed < TimeSpan.FromSeconds(1), $"took {apiResponseClock.Elapsed}");
        Assert.Equal(Assert.Single(fixTestAcceptor.ReceivedOrders).GetString(Tags.ClOrdID), fixRejectProblem.GetProperty("traceId").GetString());
        Assert.Equal(0, orderGeneratorFactory.Services.GetRequiredService<FixOrderClient>().OrdersAwaitingExecutionReportCount);
    }

    [Fact]
    public async Task Business_message_reject_of_the_order_answers_422_with_its_tag_58_in_under_1_second()
    {
        // Arrange
        using var fixTestAcceptor = StartFixTestAcceptor();
        fixTestAcceptor.ExecutionReportResponder = receivedOrder => fixTestAcceptor.BuildBusinessMessageReject(
            receivedOrder, "Mensagem de negócio recusada.", BusinessRejectReason.APPLICATION_NOT_AVAILABLE, receivedOrder.GetString(Tags.ClOrdID));
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(fixTestAcceptor.AcceptorPort);
        using var orderGeneratorClient = await CreateLoggedOnClientAsync(orderGeneratorFactory, fixTestAcceptor);

        // Act
        var apiResponseClock = Stopwatch.StartNew();
        var orderHttpResponse = await PostValidOrder(orderGeneratorClient);
        apiResponseClock.Stop();
        _measuredTimeOutput.WriteLine($"measured: {apiResponseClock.Elapsed.TotalMilliseconds:F0} ms");

        // Assert
        var fixRejectProblem = await AssertFixOrderRejectedProblem(orderHttpResponse, "Mensagem de negócio recusada.");
        Assert.True(apiResponseClock.Elapsed < TimeSpan.FromSeconds(1), $"took {apiResponseClock.Elapsed}");
        Assert.Equal(Assert.Single(fixTestAcceptor.ReceivedOrders).GetString(Tags.ClOrdID), fixRejectProblem.GetProperty("traceId").GetString());
        Assert.Equal(0, orderGeneratorFactory.Services.GetRequiredService<FixOrderClient>().OrdersAwaitingExecutionReportCount);
    }

    [Fact]
    public async Task Business_message_reject_without_BusinessRejectRefID_finds_the_order_by_RefSeqNum()
    {
        // Arrange
        using var fixTestAcceptor = StartFixTestAcceptor();
        fixTestAcceptor.ExecutionReportResponder = receivedOrder => fixTestAcceptor.BuildBusinessMessageReject(
            receivedOrder, "Recusada pelo número de sequência.", BusinessRejectReason.OTHER, businessRejectRefId: null);
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(fixTestAcceptor.AcceptorPort);
        using var orderGeneratorClient = await CreateLoggedOnClientAsync(orderGeneratorFactory, fixTestAcceptor);

        // Act
        var apiResponseClock = Stopwatch.StartNew();
        var orderHttpResponse = await PostValidOrder(orderGeneratorClient);
        apiResponseClock.Stop();
        _measuredTimeOutput.WriteLine($"measured: {apiResponseClock.Elapsed.TotalMilliseconds:F0} ms");

        // Assert
        await AssertFixOrderRejectedProblem(orderHttpResponse, "Recusada pelo número de sequência.");
        Assert.True(apiResponseClock.Elapsed < TimeSpan.FromSeconds(1), $"took {apiResponseClock.Elapsed}");
    }

    public static TheoryData<string, int?, string> FixRejectsWithoutTag58 => new()
    {
        { "session-reject", SessionRejectReason.VALUE_IS_INCORRECT, "A sessão FIX recusou a ordem (SessionRejectReason 5)." },
        { "session-reject", null, "A sessão FIX recusou a ordem." },
        { "business-message-reject", BusinessRejectReason.UNSUPPORTED_MESSAGE_TYPE, "O OrderAccumulator recusou a ordem (BusinessRejectReason 3)." }
    };

    [Theory]
    [MemberData(nameof(FixRejectsWithoutTag58))]
    public async Task Fix_reject_without_tag_58_answers_422_naming_the_reason_code(string fixRejectKind, int? rejectReasonCode, string expectedRejectText)
    {
        // Arrange
        using var fixTestAcceptor = StartFixTestAcceptor();
        fixTestAcceptor.ExecutionReportResponder = receivedOrder => fixRejectKind == "session-reject"
            ? fixTestAcceptor.BuildSessionReject(receivedOrder, rejectText: null, rejectReasonCode)
            : fixTestAcceptor.BuildBusinessMessageReject(receivedOrder, rejectText: null, rejectReasonCode!.Value, receivedOrder.GetString(Tags.ClOrdID));
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(fixTestAcceptor.AcceptorPort);
        using var orderGeneratorClient = await CreateLoggedOnClientAsync(orderGeneratorFactory, fixTestAcceptor);

        // Act
        var orderHttpResponse = await PostValidOrder(orderGeneratorClient);

        // Assert
        await AssertFixOrderRejectedProblem(orderHttpResponse, expectedRejectText);
    }

    [Fact]
    public async Task Rejects_that_point_at_another_order_do_not_answer_for_this_order()
    {
        // Arrange
        using var fixTestAcceptor = StartFixTestAcceptor();
        fixTestAcceptor.StrayExecutionReport = receivedOrder => new QuickFix.FIX44.Reject(new RefSeqNum(receivedOrder.Header.GetULong(Tags.MsgSeqNum) + 1000));
        fixTestAcceptor.ExecutionReportResponder = receivedOrder =>
            fixTestAcceptor.BuildBusinessMessageReject(receivedOrder, "De outra ordem.", BusinessRejectReason.OTHER, "outra-ordem");
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(fixTestAcceptor.AcceptorPort, executionReportTimeoutSeconds: "1");
        using var orderGeneratorClient = await CreateLoggedOnClientAsync(orderGeneratorFactory, fixTestAcceptor);

        // Act
        var orderHttpResponse = await PostValidOrder(orderGeneratorClient);

        // Assert
        await OrderCommunicationTests.AssertOrderCommunicationError(
            orderHttpResponse, "execution-report-timeout", OrderCommunicationTests.OrderMayHaveBeenAcceptedMessage);
        Assert.Equal(0, orderGeneratorFactory.Services.GetRequiredService<FixOrderClient>().OrdersAwaitingExecutionReportCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Dropped_fix_session_ends_every_waiting_order_with_503_fix_session_lost_in_under_1_second(bool acceptorSendsLogout)
    {
        // Arrange
        using var fixTestAcceptor = StartFixTestAcceptor();
        using var fixSessionTcpRelay = new FixSessionTcpRelay(fixTestAcceptor.AcceptorPort);
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(fixSessionTcpRelay.RelayPort);
        using var orderGeneratorClient = await CreateLoggedOnClientAsync(orderGeneratorFactory, fixTestAcceptor);
        Task<HttpResponseMessage>[] waitingOrderHttpResponses = [PostValidOrder(orderGeneratorClient), PostValidOrder(orderGeneratorClient)];
        await OrderGeneratorTestHost.WaitUntilTestConditionHolds(() => fixTestAcceptor.ReceivedOrders.Count == 2);

        // Act
        var sessionLossClock = Stopwatch.StartNew();
        var timesUntilEachOrderIsAnswered = waitingOrderHttpResponses
            .Select(waitingOrderHttpResponse => waitingOrderHttpResponse.ContinueWith(_ => sessionLossClock.Elapsed, TaskScheduler.Default))
            .ToArray();
        if (acceptorSendsLogout)
            fixTestAcceptor.SendLogoutToOrderGenerator();
        else
            fixSessionTcpRelay.CutFixSession();
        var orderHttpResponses = await Task.WhenAll(waitingOrderHttpResponses);
        var measuredTimesUntilAnswer = await Task.WhenAll(timesUntilEachOrderIsAnswered);
        _measuredTimeOutput.WriteLine($"measured: {string.Join(" ms, ", measuredTimesUntilAnswer.Select(measuredTime => measuredTime.TotalMilliseconds.ToString("F0")))} ms");

        // Assert
        var answeredTraceIds = new List<string>();
        foreach (var orderHttpResponse in orderHttpResponses)
        {
            var sessionLostProblem = await OrderCommunicationTests.AssertOrderCommunicationError(
                orderHttpResponse, "fix-session-lost", OrderCommunicationTests.OrderMayHaveBeenAcceptedMessage);
            answeredTraceIds.Add(sessionLostProblem.GetProperty("traceId").GetString()!);
        }
        Assert.All(measuredTimesUntilAnswer, measuredTimeUntilAnswer => Assert.True(measuredTimeUntilAnswer < TimeSpan.FromSeconds(1), $"took {measuredTimeUntilAnswer}"));
        Assert.Equal(
            fixTestAcceptor.ReceivedOrders.Select(receivedOrder => receivedOrder.GetString(Tags.ClOrdID)).Order(),
            answeredTraceIds.Order());
        Assert.Equal(0, orderGeneratorFactory.Services.GetRequiredService<FixOrderClient>().OrdersAwaitingExecutionReportCount);
    }

    [Fact]
    public async Task Deadline_of_2_seconds_from_configuration_answers_503_execution_report_timeout_in_2_seconds()
    {
        // Arrange
        using var fixTestAcceptor = StartFixTestAcceptor();
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(fixTestAcceptor.AcceptorPort, executionReportTimeoutSeconds: "2");
        using var orderGeneratorClient = await CreateLoggedOnClientAsync(orderGeneratorFactory, fixTestAcceptor);

        // Act
        var apiResponseClock = Stopwatch.StartNew();
        var orderHttpResponse = await PostValidOrder(orderGeneratorClient);
        apiResponseClock.Stop();
        _measuredTimeOutput.WriteLine($"measured: {apiResponseClock.Elapsed.TotalMilliseconds:F0} ms");

        // Assert
        await OrderCommunicationTests.AssertOrderCommunicationError(
            orderHttpResponse, "execution-report-timeout", OrderCommunicationTests.OrderMayHaveBeenAcceptedMessage);
        Assert.InRange(apiResponseClock.Elapsed, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3.5));
        Assert.Equal(0, orderGeneratorFactory.Services.GetRequiredService<FixOrderClient>().OrdersAwaitingExecutionReportCount);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("5")]
    public async Task Deadline_from_1_to_5_seconds_lets_the_app_start(string executionReportTimeoutSeconds)
    {
        // Arrange
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(
            OrderGeneratorTestHost.FindFreeTcpPort(), executionReportTimeoutSeconds: executionReportTimeoutSeconds);

        // Act
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();
        var healthHttpResponse = await orderGeneratorClient.GetAsync("/health");

        // Assert
        Assert.Equal(HttpStatusCode.OK, healthHttpResponse.StatusCode);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("6")]
    [InlineData("-1")]
    public async Task Deadline_outside_1_to_5_seconds_stops_the_start(string executionReportTimeoutSeconds)
    {
        // Arrange
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(
            OrderGeneratorTestHost.FindFreeTcpPort(), executionReportTimeoutSeconds: executionReportTimeoutSeconds);

        // Act
        var startFailure = Assert.Throws<OptionsValidationException>(() => orderGeneratorFactory.CreateClient());

        // Assert
        Assert.Contains("ExecutionReportTimeoutSeconds", startFailure.Message);
    }

    [Fact]
    public async Task Execution_report_that_arrives_after_the_deadline_logs_one_warning_with_its_clordid()
    {
        // Arrange
        using var stdoutJsonLogCapture = new StdoutJsonLogCapture();
        using var fixTestAcceptor = StartFixTestAcceptor();
        fixTestAcceptor.ExecutionReportResponder = fixTestAcceptor.BuildAcceptedExecutionReport;
        fixTestAcceptor.ExecutionReportDelay = receivedOrder => TimeSpan.FromSeconds(2);

        // Act
        await using (var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(fixTestAcceptor.AcceptorPort, executionReportTimeoutSeconds: "1"))
        {
            using var orderGeneratorClient = await CreateLoggedOnClientAsync(orderGeneratorFactory, fixTestAcceptor);
            await OrderCommunicationTests.AssertOrderCommunicationError(
                await PostValidOrder(orderGeneratorClient), "execution-report-timeout", OrderCommunicationTests.OrderMayHaveBeenAcceptedMessage);
            await OrderGeneratorTestHost.WaitUntilTestConditionHolds(() =>
                stdoutJsonLogCapture.StdoutLines.Any(stdoutLine => stdoutLine.Contains(FixOrderClient.LateExecutionReportLogMessage)));
        }

        // Assert
        var lateClOrdId = Assert.Single(fixTestAcceptor.ReceivedOrders).GetString(Tags.ClOrdID);
        Assert.True(fixTestAcceptor.SentExecutionReports.ContainsKey(lateClOrdId));
        var lateExecutionReportLine = Assert.Single(stdoutJsonLogCapture.JsonLogLines, jsonLogLine => jsonLogLine.Message == FixOrderClient.LateExecutionReportLogMessage);
        Assert.Equal(("Warning", typeof(FixOrderClient).FullName), (lateExecutionReportLine.LogLevel, lateExecutionReportLine.Category));
        Assert.Equal(lateClOrdId, lateExecutionReportLine.ReadLogField("ClOrdId"));
        Assert.Equal(lateClOrdId, lateExecutionReportLine.TraceId);
        // Only two warnings in the whole run: the 503 of the request and the late answer; no extra log per request.
        var warningOrErrorLines = stdoutJsonLogCapture.JsonLogLines
            .Where(jsonLogLine => jsonLogLine.LogLevel is "Warning" or "Error" && jsonLogLine.Category != "Microsoft.AspNetCore.StaticFiles.StaticFileMiddleware")
            .Select(jsonLogLine => (jsonLogLine.LogLevel, jsonLogLine.Category, jsonLogLine.Message))
            .ToList();
        Assert.Equal(
            [
                ("Warning", "Flowa.OrderGenerator.Entrypoint.ErrorHandling.OrderGeneratorExceptionHandler", "Expected error in request."),
                ("Warning", typeof(FixOrderClient).FullName!, FixOrderClient.LateExecutionReportLogMessage)
            ],
            warningOrErrorLines);
    }

    private static FixTestAcceptor StartFixTestAcceptor()
    {
        var fixTestAcceptor = new FixTestAcceptor(OrderGeneratorTestHost.FindFreeTcpPort());
        fixTestAcceptor.StartFixTestAcceptor();
        return fixTestAcceptor;
    }

    private static async Task<HttpClient> CreateLoggedOnClientAsync(WebApplicationFactory<Program> orderGeneratorFactory, FixTestAcceptor fixTestAcceptor)
    {
        var orderGeneratorClient = orderGeneratorFactory.CreateClient();
        await fixTestAcceptor.WaitForFixSessionLogonAsync();
        return orderGeneratorClient;
    }

    private static Task<HttpResponseMessage> PostValidOrder(HttpClient orderGeneratorClient) =>
        orderGeneratorClient.PostAsync("/api/orders", new StringContent(ValidOrderJson, Encoding.UTF8, "application/json"));

    private static async Task<JsonElement> AssertFixOrderRejectedProblem(HttpResponseMessage orderHttpResponse, string expectedRejectText)
    {
        var fixRejectProblem = await OrderApiTests.ReadProblemDetailsAsync(orderHttpResponse, HttpStatusCode.UnprocessableEntity);
        Assert.Equal(FixOrderRejectedType, fixRejectProblem.GetProperty("type").GetString());
        Assert.Equal("Regra de negócio violada", fixRejectProblem.GetProperty("title").GetString());
        Assert.Equal(expectedRejectText, fixRejectProblem.GetProperty("detail").GetString());
        Assert.Equal("BusinessRuleViolated", fixRejectProblem.GetProperty("statusResultado").GetString());
        Assert.Empty(fixRejectProblem.GetProperty("errors").EnumerateArray());
        return fixRejectProblem;
    }
}

public sealed class FixOptionsTests
{
    [Fact]
    public void Execution_report_deadline_is_5_seconds_when_the_key_is_missing()
    {
        // Arrange
        var fixOptionsWithoutDeadlineKey = new Flowa.OrderGenerator.Infrastructure.Orders.Options.FixOptions();

        // Act
        var executionReportTimeoutSeconds = fixOptionsWithoutDeadlineKey.ExecutionReportTimeoutSeconds;

        // Assert
        Assert.Equal(5, executionReportTimeoutSeconds);
    }
}
