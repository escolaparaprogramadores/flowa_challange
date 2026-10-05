using Base.OrderGenerator.Infrastructure;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using QuickFix.Fields;

namespace Base.OrderGenerator.Tests;

// OrderGenerator already logged on to a test acceptor that accepts everything, so orders can go out.
public sealed class LoggedOnOrderGenerator : IAsyncLifetime
{
    public FixTestAcceptor FixAcceptor { get; } = new(OrderGeneratorTestHost.FindFreeTcpPort());
    public WebApplicationFactory<Program> OrderGeneratorFactory { get; private set; } = null!;
    public HttpClient OrderGeneratorClient { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        FixAcceptor.StartFixTestAcceptor();
        OrderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(FixAcceptor.AcceptorPort);
        OrderGeneratorClient = OrderGeneratorFactory.CreateClient();
        await FixAcceptor.WaitForFixSessionLogonAsync();
    }

    public async Task DisposeAsync()
    {
        OrderGeneratorClient.Dispose();
        await OrderGeneratorFactory.DisposeAsync();
        FixAcceptor.Dispose();
    }
}

public sealed class OrderApiTests : IClassFixture<LoggedOnOrderGenerator>
{
    private const string SentinelOrderJson = """{"symbol":"PETR4","side":"sell","quantity":7,"price":7.77}""";

    private readonly LoggedOnOrderGenerator _loggedOnOrderGenerator;

    public OrderApiTests(LoggedOnOrderGenerator loggedOnOrderGenerator)
    {
        _loggedOnOrderGenerator = loggedOnOrderGenerator;
        _loggedOnOrderGenerator.FixAcceptor.ResetToAcceptEveryOrder();
    }

    // Decision 24: what a NewOrderSingle cannot carry stops at the door with a 400.
    public static TheoryData<string, string> OrdersThatDoNotFitFix => new()
    {
        { """{"side":"buy","quantity":100,"price":10.50}""", "Informe o símbolo." },
        { """{"symbol":"PE\u0001TR4","side":"buy","quantity":100,"price":10.50}""", "O símbolo não pode ter caractere de controle." },
        { """{"symbol":"PETR4","side":"compra","quantity":100,"price":10.50}""", "Lado inválido. Use compra ou venda." },
        { """{"symbol":"PETR4","side":"BUY","quantity":100,"price":10.50}""", "Lado inválido. Use compra ou venda." },
        { """{"symbol":"PETR4","quantity":100,"price":10.50}""", "Informe o lado da ordem." },
        { """{"symbol":"PETR4","side":"buy","quantity":"abc","price":10.50}""", "A quantidade deve ser um número inteiro." },
        { """{"symbol":"PETR4","side":"buy","quantity":1e3,"price":10.50}""", "A quantidade deve ser um número inteiro." },
        { """{"symbol":"PETR4","side":"buy","price":10.50}""", "Informe a quantidade." },
        { """{"symbol":"PETR4","side":"buy","quantity":100,"price":"abc"}""", "O preço deve ser um número." },
        { """{"symbol":"PETR4","side":"buy","quantity":100,"price":10.0000000000000000000000000001}""", "O preço deve ser um número." },
        { """{"symbol":"PETR4","side":"buy","quantity":100}""", "Informe o preço." },
    };

    // Decision 24 and CA-5: the 400 of format is a problem+json with the field message in pt-BR.
    [Theory]
    [MemberData(nameof(OrdersThatDoNotFitFix))]
    public async Task Order_that_does_not_fit_fix_gets_400_problem_in_portuguese_and_sends_nothing(
        string orderJson, string expectedOrderFieldFormatMessage)
    {
        var badFormatHttpResponse = await PostOrderJson(orderJson);

        var invalidOrderProblem = await ReadProblemDetailsAsync(badFormatHttpResponse, HttpStatusCode.BadRequest);
        Assert.Equal("urn:base-investimentos:problem:invalid-order", invalidOrderProblem.GetProperty("type").GetString());
        Assert.Equal("Dados inválidos", invalidOrderProblem.GetProperty("title").GetString());
        Assert.Equal("A ordem tem campos inválidos.", invalidOrderProblem.GetProperty("detail").GetString());
        Assert.Equal("InvalidInput", invalidOrderProblem.GetProperty("statusResultado").GetString());
        Assert.Equal([expectedOrderFieldFormatMessage], ReadProblemErrorMessages(invalidOrderProblem));
        await AssertOnlySentinelReachedAcceptor();
    }

    // Decisions 12 and 13: the field rule belongs to the OrderAccumulator, so these orders go out by FIX
    // exactly as typed and the answer is whatever came back in the ExecutionReport.
    public static TheoryData<string, string, char, decimal, decimal> OrdersWithFieldsOnlyTheAccumulatorJudges => new()
    {
        { """{"symbol":"XPTO3","side":"buy","quantity":100,"price":10.50}""", "XPTO3", '1', 100m, 10.50m },
        { """{"symbol":"petr4","side":"buy","quantity":100,"price":10.50}""", "petr4", '1', 100m, 10.50m },
        { """{"symbol":"PETR4","side":"buy","quantity":0,"price":10.50}""", "PETR4", '1', 0m, 10.50m },
        { """{"symbol":"PETR4","side":"buy","quantity":-1,"price":10.50}""", "PETR4", '1', -1m, 10.50m },
        { """{"symbol":"PETR4","side":"buy","quantity":1.5,"price":10.50}""", "PETR4", '1', 1.5m, 10.50m },
        { """{"symbol":"PETR4","side":"sell","quantity":100000,"price":10.50}""", "PETR4", '2', 100000m, 10.50m },
        { """{"symbol":"PETR4","side":"buy","quantity":100,"price":0}""", "PETR4", '1', 100m, 0m },
        { """{"symbol":"PETR4","side":"buy","quantity":100,"price":-1}""", "PETR4", '1', 100m, -1m },
        { """{"symbol":"PETR4","side":"buy","quantity":100,"price":1000}""", "PETR4", '1', 100m, 1000m },
        { """{"symbol":"PETR4","side":"buy","quantity":100,"price":10.005}""", "PETR4", '1', 100m, 10.005m },
        { """{"symbol":"ITUB4","side":"buy","quantity":100000,"price":1000}""", "ITUB4", '1', 100000m, 1000m },
    };

    [Theory]
    [MemberData(nameof(OrdersWithFieldsOnlyTheAccumulatorJudges))]
    public async Task Order_with_invalid_field_goes_by_fix_and_returns_the_accumulator_rejection(
        string orderJson, string expectedSymbol, char expectedSideFixCode, decimal expectedQuantity, decimal expectedPrice)
    {
        const string accumulatorRejectionText = "Texto da tag 58 vindo do OrderAccumulator.";
        _loggedOnOrderGenerator.FixAcceptor.ExecutionReportResponder = receivedOrder =>
            _loggedOnOrderGenerator.FixAcceptor.BuildRejectedExecutionReport(receivedOrder, accumulatorRejectionText);

        var orderHttpResponse = await PostOrderJson(orderJson);

        var sentNewOrderSingle = Assert.Single(_loggedOnOrderGenerator.FixAcceptor.ReceivedOrders);
        Assert.Equal(expectedSymbol, sentNewOrderSingle.GetString(Tags.Symbol));
        Assert.Equal(expectedSideFixCode, sentNewOrderSingle.GetChar(Tags.Side));
        Assert.Equal(expectedQuantity, sentNewOrderSingle.GetDecimal(Tags.OrderQty));
        Assert.Equal(expectedPrice, sentNewOrderSingle.GetDecimal(Tags.Price));
        var rejectedOrderMessage = await ReadSuccessDataMessageAsync(orderHttpResponse);
        var rejectedResponse = rejectedOrderMessage.GetProperty("data");
        Assert.Equal("rejected", rejectedResponse.GetProperty("status").GetString());
        Assert.Equal(accumulatorRejectionText, rejectedOrderMessage.GetProperty("message").GetString());
        Assert.Equal(sentNewOrderSingle.GetString(Tags.ClOrdID), rejectedResponse.GetProperty("clOrdId").GetString());
        Assert.Equal(expectedSymbol, rejectedResponse.GetProperty("symbol").GetString());
        Assert.Equal(expectedQuantity, rejectedResponse.GetProperty("quantity").GetDecimal());
        Assert.Equal(expectedPrice, rejectedResponse.GetProperty("price").GetDecimal());
        AssertAnswersItsOwnExecutionReport(rejectedResponse);
    }

    [Fact]
    public async Task Body_that_is_not_json_gets_400_with_the_four_required_fields()
    {
        var notJsonHttpResponse = await PostOrderJson("this is not json");

        var invalidOrderProblem = await ReadProblemDetailsAsync(notJsonHttpResponse, HttpStatusCode.BadRequest);
        Assert.Equal("A ordem tem campos inválidos.", invalidOrderProblem.GetProperty("detail").GetString());
        Assert.Equal(FourRequiredOrderFieldMessages, ReadProblemErrorMessages(invalidOrderProblem));
        await AssertOnlySentinelReachedAcceptor();
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("""{"symbol":null,"side":null,"quantity":null,"price":null}""")]
    public async Task Json_without_the_order_fields_gets_400_with_the_four_required_fields(string orderJsonWithoutFields)
    {
        var emptyOrderHttpResponse = await PostOrderJson(orderJsonWithoutFields);

        var invalidOrderProblem = await ReadProblemDetailsAsync(emptyOrderHttpResponse, HttpStatusCode.BadRequest);
        Assert.Equal("A ordem tem campos inválidos.", invalidOrderProblem.GetProperty("detail").GetString());
        Assert.Equal(FourRequiredOrderFieldMessages, ReadProblemErrorMessages(invalidOrderProblem));
        await AssertOnlySentinelReachedAcceptor();
    }

    [Theory]
    [InlineData("buy", '1')]
    [InlineData("sell", '2')]
    public async Task Buy_and_sell_go_out_as_NewOrderSingle_and_come_back_accepted(string orderSideJson, char orderSideFixCode)
    {
        // TransactTime goes in UTC with milliseconds; the window accepts that rounding.
        var orderSentNotBeforeUtc = DateTime.UtcNow.AddMilliseconds(-1);
        var orderHttpResponse = await PostOrderJson($$"""{"symbol":"VALE3","side":"{{orderSideJson}}","quantity":250,"price":61.37}""");
        var orderSentNotAfterUtc = DateTime.UtcNow;

        Assert.Equal(HttpStatusCode.OK, orderHttpResponse.StatusCode);
        var sentNewOrderSingle = Assert.Single(_loggedOnOrderGenerator.FixAcceptor.ReceivedOrders);
        Assert.Equal("D", sentNewOrderSingle.Header.GetString(Tags.MsgType));
        Assert.Equal("VALE3", sentNewOrderSingle.GetString(Tags.Symbol));
        Assert.Equal(orderSideFixCode, sentNewOrderSingle.GetChar(Tags.Side));
        Assert.Equal(250m, sentNewOrderSingle.GetDecimal(Tags.OrderQty));
        Assert.Equal(61.37m, sentNewOrderSingle.GetDecimal(Tags.Price));
        Assert.Equal(OrdType.LIMIT, sentNewOrderSingle.GetChar(Tags.OrdType));
        Assert.InRange(sentNewOrderSingle.GetDateTime(Tags.TransactTime), orderSentNotBeforeUtc, orderSentNotAfterUtc);

        var acceptedOrderMessage = await ReadSuccessDataMessageAsync(orderHttpResponse);
        var orderResponse = acceptedOrderMessage.GetProperty("data");
        var clOrdId = sentNewOrderSingle.GetString(Tags.ClOrdID);
        Assert.Equal("accepted", orderResponse.GetProperty("status").GetString());
        Assert.Equal(clOrdId, orderResponse.GetProperty("clOrdId").GetString());
        Assert.Matches("^[0-9a-f]{32}$", clOrdId);
        AssertAnswersItsOwnExecutionReport(orderResponse);
        Assert.Equal("VALE3", orderResponse.GetProperty("symbol").GetString());
        Assert.Equal(orderSideJson, orderResponse.GetProperty("side").GetString());
        Assert.Equal(250, orderResponse.GetProperty("quantity").GetInt32());
        Assert.Equal(61.37m, orderResponse.GetProperty("price").GetDecimal());
        Assert.Equal("Ordem aceita.", acceptedOrderMessage.GetProperty("message").GetString());
        // CA-4: no second envelope and no field outside the contract inside "data".
        Assert.Equal(
            ["status", "clOrdId", "orderId", "execId", "symbol", "side", "quantity", "price"],
            orderResponse.EnumerateObject().Select(orderResponseField => orderResponseField.Name));
    }

    [Theory]
    [InlineData("1", "0.01")]
    [InlineData("99999", "999.99")]
    [InlineData("\"1\"", "\"0.01\"")]
    public async Task Accepted_quantity_and_price_edges_go_out_by_fix(string orderQuantityJson, string orderPriceJson)
    {
        var orderHttpResponse = await PostOrderJson($$"""{"symbol":"VIIA4","side":"sell","quantity":{{orderQuantityJson}},"price":{{orderPriceJson}}}""");

        var expectedOrderQuantity = int.Parse(orderQuantityJson.Trim('"'));
        var expectedOrderPrice = decimal.Parse(orderPriceJson.Trim('"'), System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(HttpStatusCode.OK, orderHttpResponse.StatusCode);
        var sentNewOrderSingle = Assert.Single(_loggedOnOrderGenerator.FixAcceptor.ReceivedOrders);
        Assert.Equal("VIIA4", sentNewOrderSingle.GetString(Tags.Symbol));
        Assert.Equal(Side.SELL, sentNewOrderSingle.GetChar(Tags.Side));
        Assert.Equal(expectedOrderQuantity, sentNewOrderSingle.GetDecimal(Tags.OrderQty));
        Assert.Equal(expectedOrderPrice, sentNewOrderSingle.GetDecimal(Tags.Price));
        var orderResponse = await ReadOrderDataAsync(orderHttpResponse);
        Assert.Equal("accepted", orderResponse.GetProperty("status").GetString());
        Assert.Equal(expectedOrderQuantity, orderResponse.GetProperty("quantity").GetInt32());
        Assert.Equal(expectedOrderPrice, orderResponse.GetProperty("price").GetDecimal());
    }

    [Fact]
    public async Task Rejected_order_returns_the_tag_58_text()
    {
        const string rejectionText = "Ordem rejeitada: a exposição de PETR4 passaria do limite de 100.000.000,00.";
        _loggedOnOrderGenerator.FixAcceptor.ExecutionReportResponder = receivedOrder => _loggedOnOrderGenerator.FixAcceptor.BuildRejectedExecutionReport(receivedOrder, rejectionText);

        var orderHttpResponse = await PostOrderJson("""{"symbol":"PETR4","side":"buy","quantity":99999,"price":999.99}""");

        var rejectedOrderMessage = await ReadSuccessDataMessageAsync(orderHttpResponse);
        var rejectedResponse = rejectedOrderMessage.GetProperty("data");
        var clOrdId = Assert.Single(_loggedOnOrderGenerator.FixAcceptor.ReceivedOrders).GetString(Tags.ClOrdID);
        Assert.Equal("rejected", rejectedResponse.GetProperty("status").GetString());
        Assert.Equal(rejectionText, rejectedOrderMessage.GetProperty("message").GetString());
        Assert.Equal(clOrdId, rejectedResponse.GetProperty("clOrdId").GetString());
        AssertAnswersItsOwnExecutionReport(rejectedResponse);
        Assert.Equal("PETR4", rejectedResponse.GetProperty("symbol").GetString());
        Assert.Equal("buy", rejectedResponse.GetProperty("side").GetString());
        Assert.Equal(99999, rejectedResponse.GetProperty("quantity").GetInt32());
        Assert.Equal(999.99m, rejectedResponse.GetProperty("price").GetDecimal());
    }

    [Fact]
    public async Task Rejected_order_without_tag_58_returns_the_default_message()
    {
        _loggedOnOrderGenerator.FixAcceptor.ExecutionReportResponder = receivedOrder =>
            _loggedOnOrderGenerator.FixAcceptor.BuildExecutionReport(receivedOrder, ExecType.REJECTED, OrdStatus.REJECTED, 0);

        var orderHttpResponse = await PostOrderJson("""{"symbol":"VALE3","side":"sell","quantity":4,"price":12.34}""");

        var rejectedOrderMessage = await ReadSuccessDataMessageAsync(orderHttpResponse);
        var rejectedResponse = rejectedOrderMessage.GetProperty("data");
        Assert.Equal("rejected", rejectedResponse.GetProperty("status").GetString());
        Assert.Equal("Ordem rejeitada.", rejectedOrderMessage.GetProperty("message").GetString());
        AssertAnswersItsOwnExecutionReport(rejectedResponse);
    }

    [Fact]
    public async Task ExecutionReport_with_ExecType_outside_the_contract_answers_500()
    {
        // 150=2 (Fill) is not in contract v1, which only has 0 (New) and 8 (Rejected).
        _loggedOnOrderGenerator.FixAcceptor.ExecutionReportResponder = receivedOrder =>
            _loggedOnOrderGenerator.FixAcceptor.BuildExecutionReport(receivedOrder, ExecType.FILL, OrdStatus.FILLED, 0);

        var orderHttpResponse = await PostOrderJson("""{"symbol":"PETR4","side":"buy","quantity":10,"price":20.00}""");

        var unexpectedErrorProblem = await ReadProblemDetailsAsync(orderHttpResponse, HttpStatusCode.InternalServerError);
        Assert.Equal("urn:base-investimentos:problem:internal-error", unexpectedErrorProblem.GetProperty("type").GetString());
        Assert.Equal("Erro interno", unexpectedErrorProblem.GetProperty("title").GetString());
        Assert.Equal("Aconteceu um erro inesperado. Informe o traceId ao suporte.", unexpectedErrorProblem.GetProperty("detail").GetString());
        Assert.Equal("InternalError", unexpectedErrorProblem.GetProperty("statusResultado").GetString());
        Assert.Empty(unexpectedErrorProblem.GetProperty("errors").EnumerateArray());
        // CA-11: the order already had its ClOrdID, so the answer carries it.
        Assert.Equal(Assert.Single(_loggedOnOrderGenerator.FixAcceptor.ReceivedOrders).GetString(Tags.ClOrdID), unexpectedErrorProblem.GetProperty("traceId").GetString());
        // The technical text of the failure stays in the log.
        Assert.DoesNotContain("ExecutionReport", await orderHttpResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Report_with_another_ClOrdID_does_not_answer_for_the_order()
    {
        _loggedOnOrderGenerator.FixAcceptor.StrayExecutionReport = receivedOrder =>
            _loggedOnOrderGenerator.FixAcceptor.BuildExecutionReport(receivedOrder, ExecType.REJECTED, OrdStatus.REJECTED, 0, clOrdId: "not-this-order");

        var orderResponse = await ReadOrderDataAsync(await PostOrderJson("""{"symbol":"VIIA4","side":"buy","quantity":5,"price":3.21}"""));

        Assert.Equal("accepted", orderResponse.GetProperty("status").GetString());
        AssertAnswersItsOwnExecutionReport(orderResponse);
        var strayOrderId = _loggedOnOrderGenerator.FixAcceptor.SentExecutionReports["not-this-order"].GetString(Tags.OrderID);
        Assert.NotEqual(strayOrderId, orderResponse.GetProperty("orderId").GetString());
    }

    [Fact]
    public async Task Simultaneous_orders_each_get_their_own_answer()
    {
        // The PETR4 one is answered after the VALE3 one: the answers arrive out of sending order.
        const string rejectionText = "Ordem rejeitada: a exposição de VALE3 passaria do limite de 100.000.000,00.";
        _loggedOnOrderGenerator.FixAcceptor.ExecutionReportResponder = receivedOrder => receivedOrder.GetString(Tags.Symbol) == "VALE3"
            ? _loggedOnOrderGenerator.FixAcceptor.BuildRejectedExecutionReport(receivedOrder, rejectionText)
            : _loggedOnOrderGenerator.FixAcceptor.BuildAcceptedExecutionReport(receivedOrder);
        _loggedOnOrderGenerator.FixAcceptor.ExecutionReportDelay = receivedOrder =>
            receivedOrder.GetString(Tags.Symbol) == "PETR4" ? TimeSpan.FromMilliseconds(500) : TimeSpan.Zero;

        var petr4Request = PostOrderJson("""{"symbol":"PETR4","side":"buy","quantity":10,"price":30.00}""");
        var vale3Request = PostOrderJson("""{"symbol":"VALE3","side":"sell","quantity":20,"price":60.00}""");
        await Task.WhenAll(petr4Request, vale3Request);

        var petr4Response = await ReadOrderDataAsync(await petr4Request);
        var vale3OrderMessage = await ReadSuccessDataMessageAsync(await vale3Request);
        var vale3Response = vale3OrderMessage.GetProperty("data");
        Assert.Equal("accepted", petr4Response.GetProperty("status").GetString());
        Assert.Equal("PETR4", petr4Response.GetProperty("symbol").GetString());
        Assert.Equal("rejected", vale3Response.GetProperty("status").GetString());
        Assert.Equal(rejectionText, vale3OrderMessage.GetProperty("message").GetString());
        AssertAnswersItsOwnExecutionReport(petr4Response);
        AssertAnswersItsOwnExecutionReport(vale3Response);
    }

    [Fact]
    public async Task Answer_that_arrives_after_5_seconds_is_dropped()
    {
        _loggedOnOrderGenerator.FixAcceptor.ExecutionReportDelay = receivedOrder => TimeSpan.FromSeconds(6);

        var lateOrderHttpResponse = await PostOrderJson("""{"symbol":"PETR4","side":"buy","quantity":10,"price":30.00}""");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, lateOrderHttpResponse.StatusCode);
        var lateClOrdId = Assert.Single(_loggedOnOrderGenerator.FixAcceptor.ReceivedOrders).GetString(Tags.ClOrdID);
        await OrderGeneratorTestHost.WaitUntilTestConditionHolds(() => _loggedOnOrderGenerator.FixAcceptor.SentExecutionReports.ContainsKey(lateClOrdId));

        _loggedOnOrderGenerator.FixAcceptor.ExecutionReportDelay = receivedOrder => TimeSpan.Zero;
        var nextOrderResponse = await ReadOrderDataAsync(await PostOrderJson("""{"symbol":"VALE3","side":"buy","quantity":10,"price":30.00}"""));

        Assert.Equal("accepted", nextOrderResponse.GetProperty("status").GetString());
        AssertAnswersItsOwnExecutionReport(nextOrderResponse);
        Assert.Equal(0, _loggedOnOrderGenerator.OrderGeneratorFactory.Services.GetRequiredService<FixOrderClient>().OrdersAwaitingExecutionReportCount);
    }

    // The FIX session delivers in order: if an invalid order had gone out, it would arrive before the sentinel.
    private async Task AssertOnlySentinelReachedAcceptor()
    {
        var sentinelResponse = await ReadOrderDataAsync(await PostOrderJson(SentinelOrderJson));
        var sentinelClOrdId = sentinelResponse.GetProperty("clOrdId").GetString();
        var receivedOrder = Assert.Single(_loggedOnOrderGenerator.FixAcceptor.ReceivedOrders);
        Assert.Equal(sentinelClOrdId, receivedOrder.GetString(Tags.ClOrdID));
    }

    private void AssertAnswersItsOwnExecutionReport(JsonElement orderResponse)
    {
        var executionReport = _loggedOnOrderGenerator.FixAcceptor.SentExecutionReports[orderResponse.GetProperty("clOrdId").GetString()!];
        Assert.Equal(executionReport.GetString(Tags.OrderID), orderResponse.GetProperty("orderId").GetString());
        Assert.Equal(executionReport.GetString(Tags.ExecID), orderResponse.GetProperty("execId").GetString());
    }

    private Task<HttpResponseMessage> PostOrderJson(string orderJson) =>
        _loggedOnOrderGenerator.OrderGeneratorClient.PostAsync("/api/orders", new StringContent(orderJson, Encoding.UTF8, "application/json"));

    private static readonly string[] FourRequiredOrderFieldMessages =
        ["Informe o símbolo.", "Informe o lado da ordem.", "Informe a quantidade.", "Informe o preço."];

    internal static async Task<JsonElement> ReadOrderGeneratorResponseJson(HttpResponseMessage orderGeneratorHttpResponse) =>
        JsonDocument.Parse(await orderGeneratorHttpResponse.Content.ReadAsStringAsync()).RootElement;

    // CA-4: every success of /api is a DataMessage with these exact envelope values; "data" is the route body.
    internal static async Task<JsonElement> ReadSuccessDataMessageAsync(HttpResponseMessage successHttpResponse)
    {
        Assert.Equal(HttpStatusCode.OK, successHttpResponse.StatusCode);
        Assert.Equal("application/json", successHttpResponse.Content.Headers.ContentType?.MediaType);
        var successDataMessage = await ReadOrderGeneratorResponseJson(successHttpResponse);
        Assert.Equal(
            ["success", "status", "message", "data", "errors", "errorCode"],
            successDataMessage.EnumerateObject().Select(dataMessageField => dataMessageField.Name));
        Assert.True(successDataMessage.GetProperty("success").GetBoolean());
        Assert.Equal("Ok", successDataMessage.GetProperty("status").GetString());
        Assert.Empty(successDataMessage.GetProperty("errors").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, successDataMessage.GetProperty("errorCode").ValueKind);
        return successDataMessage;
    }

    internal static async Task<JsonElement> ReadOrderDataAsync(HttpResponseMessage orderHttpResponse) =>
        (await ReadSuccessDataMessageAsync(orderHttpResponse)).GetProperty("data");

    // CA-5: every HTTP error of /api is a problem+json whose status matches the HTTP one, with the compatibility
    // fields and a 32 hex traceId (the trace id of the request).
    internal static async Task<JsonElement> ReadProblemDetailsAsync(HttpResponseMessage errorHttpResponse, HttpStatusCode expectedHttpStatus)
    {
        Assert.Equal(expectedHttpStatus, errorHttpResponse.StatusCode);
        Assert.Equal("application/problem+json", errorHttpResponse.Content.Headers.ContentType?.MediaType);
        var responseProblemDetails = await ReadOrderGeneratorResponseJson(errorHttpResponse);
        Assert.Equal((int)expectedHttpStatus, responseProblemDetails.GetProperty("status").GetInt32());
        Assert.Equal(errorHttpResponse.RequestMessage!.RequestUri!.AbsolutePath, responseProblemDetails.GetProperty("instance").GetString());
        Assert.Matches("^[0-9a-f]{32}$", responseProblemDetails.GetProperty("traceId").GetString());
        Assert.False(responseProblemDetails.GetProperty("success").GetBoolean());
        return responseProblemDetails;
    }

    internal static List<string> ReadProblemErrorMessages(JsonElement responseProblemDetails) =>
        responseProblemDetails.GetProperty("errors").EnumerateArray().Select(problemErrorMessage => problemErrorMessage.GetString()!).ToList();
}

// CA-19: without the other end, or with it silent, the API answers in Portuguese within the deadline and holds nothing.
public sealed class OrderCommunicationTests
{
    private const string ValidOrderJson = """{"symbol":"PETR4","side":"buy","quantity":100,"price":10.50}""";
    private const string OrderCommunicationMessage = "Não foi possível falar com o OrderAccumulator. Tente de novo em instantes.";

    [Fact]
    public async Task Without_a_fix_session_answers_503_right_away()
    {
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort());
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();

        var apiResponseClock = Stopwatch.StartNew();
        var orderHttpResponse = await PostValidOrder(orderGeneratorClient);
        apiResponseClock.Stop();

        await AssertOrderCommunicationError(orderHttpResponse, "fix-session-not-logged-on");
        Assert.True(apiResponseClock.Elapsed < TimeSpan.FromSeconds(1), $"took {apiResponseClock.Elapsed}");
        Assert.Equal(0, orderGeneratorFactory.Services.GetRequiredService<FixOrderClient>().OrdersAwaitingExecutionReportCount);
    }

    [Fact]
    public void Test_acceptor_listens_only_on_loopback()
    {
        // Listening on every network, Windows asks the owner to allow the testhost through the firewall.
        using var fixTestAcceptor = new FixTestAcceptor(OrderGeneratorTestHost.FindFreeTcpPort());
        fixTestAcceptor.StartFixTestAcceptor();

        var acceptorListeners = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners().Where(listenerEndpoint => listenerEndpoint.Port == fixTestAcceptor.AcceptorPort).ToList();

        var acceptorListener = Assert.Single(acceptorListeners);
        Assert.Equal(IPAddress.Loopback, acceptorListener.Address);
    }

    [Fact]
    public async Task Silent_acceptor_answers_503_in_5_seconds_and_drops_the_wait()
    {
        using var fixTestAcceptor = new FixTestAcceptor(OrderGeneratorTestHost.FindFreeTcpPort());
        fixTestAcceptor.StartFixTestAcceptor();
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(fixTestAcceptor.AcceptorPort);
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();
        await fixTestAcceptor.WaitForFixSessionLogonAsync();

        var apiResponseClock = Stopwatch.StartNew();
        var orderHttpResponse = await PostValidOrder(orderGeneratorClient);
        apiResponseClock.Stop();

        await AssertOrderCommunicationError(orderHttpResponse, "execution-report-timeout");
        Assert.Single(fixTestAcceptor.ReceivedOrders);
        Assert.InRange(apiResponseClock.Elapsed, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(6.5));
        Assert.Equal(0, orderGeneratorFactory.Services.GetRequiredService<FixOrderClient>().OrdersAwaitingExecutionReportCount);
    }

    [Fact]
    public async Task When_the_acceptor_comes_back_the_initiator_logs_on_again_and_only_the_new_order_goes_through()
    {
        using var fixTestAcceptor = new FixTestAcceptor(OrderGeneratorTestHost.FindFreeTcpPort());
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(fixTestAcceptor.AcceptorPort);
        using var orderGeneratorClient = orderGeneratorFactory.CreateClient();

        await AssertOrderCommunicationError(await PostValidOrder(orderGeneratorClient), "fix-session-not-logged-on");

        fixTestAcceptor.ExecutionReportResponder = fixTestAcceptor.BuildAcceptedExecutionReport;
        fixTestAcceptor.StartFixTestAcceptor();
        await fixTestAcceptor.WaitForFixSessionLogonAsync();

        var orderResponse = await OrderApiTests.ReadOrderDataAsync(await PostValidOrder(orderGeneratorClient));
        Assert.Equal("accepted", orderResponse.GetProperty("status").GetString());
        // D-34: the order refused without a session did not stay in the store to go out after the logon.
        var receivedOrder = Assert.Single(fixTestAcceptor.ReceivedOrders);
        Assert.Equal(orderResponse.GetProperty("clOrdId").GetString(), receivedOrder.GetString(Tags.ClOrdID));
    }

    private static Task<HttpResponseMessage> PostValidOrder(HttpClient orderGeneratorClient) =>
        orderGeneratorClient.PostAsync("/api/orders", new StringContent(ValidOrderJson, Encoding.UTF8, "application/json"));

    private static async Task AssertOrderCommunicationError(HttpResponseMessage orderHttpResponse, string expectedErrorCode)
    {
        var communicationErrorProblem = await OrderApiTests.ReadProblemDetailsAsync(orderHttpResponse, HttpStatusCode.ServiceUnavailable);
        Assert.Equal("urn:base-investimentos:problem:" + expectedErrorCode, communicationErrorProblem.GetProperty("type").GetString());
        Assert.Equal("Serviço indisponível", communicationErrorProblem.GetProperty("title").GetString());
        Assert.Equal(OrderCommunicationMessage, communicationErrorProblem.GetProperty("detail").GetString());
        Assert.Equal("ServiceUnavailable", communicationErrorProblem.GetProperty("statusResultado").GetString());
        Assert.Empty(communicationErrorProblem.GetProperty("errors").EnumerateArray());
    }
}
