using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Flowa.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using QuickFix.Fields;

namespace OrderGenerator.Tests;

// OrderGenerator já logado num acceptor de teste que aceita tudo, para as ordens poderem sair.
public sealed class LoggedOnGenerator : IAsyncLifetime
{
    public TestAcceptor Acceptor { get; } = new(TestHost.FreePort());
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Acceptor.Start();
        Factory = TestHost.CreateOrderGeneratorFactory(Acceptor.Port);
        Client = Factory.CreateClient();
        await Acceptor.WaitForLogonAsync();
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
        Acceptor.Dispose();
    }
}

public sealed class OrderApiTests : IClassFixture<LoggedOnGenerator>
{
    private const string SentinelOrderJson = """{"symbol":"PETR4","side":"sell","quantity":7,"price":7.77}""";

    private readonly LoggedOnGenerator _generator;

    public OrderApiTests(LoggedOnGenerator generator)
    {
        _generator = generator;
        _generator.Acceptor.ResetToAcceptEveryOrder();
    }

    public static TheoryData<string, string, string> InvalidOrders => new()
    {
        // CA-1
        { """{"symbol":"XPTO3","side":"buy","quantity":100,"price":10.50}""", OrderFields.OrderSymbolFieldName, OrderMessages.OrderSymbolInvalidMessage },
        { """{"symbol":"petr4","side":"buy","quantity":100,"price":10.50}""", OrderFields.OrderSymbolFieldName, OrderMessages.OrderSymbolInvalidMessage },
        { """{"side":"buy","quantity":100,"price":10.50}""", OrderFields.OrderSymbolFieldName, OrderMessages.OrderSymbolRequiredMessage },
        // CA-2
        { """{"symbol":"PETR4","side":"compra","quantity":100,"price":10.50}""", OrderFields.OrderSideFieldName, OrderMessages.OrderSideInvalidMessage },
        { """{"symbol":"PETR4","side":"BUY","quantity":100,"price":10.50}""", OrderFields.OrderSideFieldName, OrderMessages.OrderSideInvalidMessage },
        { """{"symbol":"PETR4","quantity":100,"price":10.50}""", OrderFields.OrderSideFieldName, OrderMessages.OrderSideRequiredMessage },
        // CA-3
        { """{"symbol":"PETR4","side":"buy","quantity":0,"price":10.50}""", OrderFields.OrderQuantityFieldName, OrderMessages.OrderQuantityNotPositiveMessage },
        { """{"symbol":"PETR4","side":"buy","quantity":-1,"price":10.50}""", OrderFields.OrderQuantityFieldName, OrderMessages.OrderQuantityNotPositiveMessage },
        { """{"symbol":"PETR4","side":"buy","quantity":1.5,"price":10.50}""", OrderFields.OrderQuantityFieldName, OrderMessages.OrderQuantityNotIntegerMessage },
        { """{"symbol":"PETR4","side":"buy","quantity":"abc","price":10.50}""", OrderFields.OrderQuantityFieldName, OrderMessages.OrderQuantityNotIntegerMessage },
        { """{"symbol":"PETR4","side":"buy","quantity":100000,"price":10.50}""", OrderFields.OrderQuantityFieldName, OrderMessages.OrderQuantityTooLargeMessage },
        // CA-4
        { """{"symbol":"PETR4","side":"buy","quantity":100,"price":0}""", OrderFields.OrderPriceFieldName, OrderMessages.OrderPriceNotPositiveMessage },
        { """{"symbol":"PETR4","side":"buy","quantity":100,"price":-1}""", OrderFields.OrderPriceFieldName, OrderMessages.OrderPriceNotPositiveMessage },
        { """{"symbol":"PETR4","side":"buy","quantity":100,"price":1000}""", OrderFields.OrderPriceFieldName, OrderMessages.OrderPriceTooLargeMessage },
        { """{"symbol":"PETR4","side":"buy","quantity":100,"price":10.005}""", OrderFields.OrderPriceFieldName, OrderMessages.OrderPriceOffTickMessage },
        { """{"symbol":"PETR4","side":"buy","quantity":100,"price":"abc"}""", OrderFields.OrderPriceFieldName, OrderMessages.OrderPriceNotNumberMessage },
    };

    [Theory]
    [MemberData(nameof(InvalidOrders))]
    public async Task Campo_invalido_responde_400_em_portugues_e_nao_envia_FIX(string invalidOrderJson, string expectedField, string expectedMessage)
    {
        var invalidOrderHttpResponse = await PostOrderJson(invalidOrderJson);

        Assert.Equal(HttpStatusCode.BadRequest, invalidOrderHttpResponse.StatusCode);
        var validationErrorResponse = await ReadJson(invalidOrderHttpResponse);
        Assert.Equal("validation_error", validationErrorResponse.GetProperty("status").GetString());
        Assert.Equal("A ordem tem campos inválidos.", validationErrorResponse.GetProperty("message").GetString());
        var fieldError = Assert.Single(validationErrorResponse.GetProperty("errors").EnumerateArray());
        Assert.Equal(expectedField, fieldError.GetProperty("field").GetString());
        Assert.Equal(expectedMessage, fieldError.GetProperty("message").GetString());
        await AssertOnlySentinelReachedAcceptor();
    }

    [Fact]
    public async Task Corpo_que_nao_e_JSON_responde_400_com_os_quatro_campos_obrigatorios()
    {
        var notJsonHttpResponse = await PostOrderJson("isto não é json");

        Assert.Equal(HttpStatusCode.BadRequest, notJsonHttpResponse.StatusCode);
        var fieldErrors = (await ReadJson(notJsonHttpResponse)).GetProperty("errors").EnumerateArray()
            .Select(fieldError => (fieldError.GetProperty("field").GetString(), fieldError.GetProperty("message").GetString()))
            .ToList();
        Assert.Equal(
            [
                (OrderFields.OrderSymbolFieldName, OrderMessages.OrderSymbolRequiredMessage),
                (OrderFields.OrderSideFieldName, OrderMessages.OrderSideRequiredMessage),
                (OrderFields.OrderQuantityFieldName, OrderMessages.OrderQuantityRequiredMessage),
                (OrderFields.OrderPriceFieldName, OrderMessages.OrderPriceRequiredMessage)
            ],
            fieldErrors);
        await AssertOnlySentinelReachedAcceptor();
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("""{"symbol":null,"side":null,"quantity":null,"price":null}""")]
    public async Task JSON_sem_os_campos_da_ordem_responde_400_com_os_quatro_campos_obrigatorios(string orderJsonWithoutFields)
    {
        var emptyOrderHttpResponse = await PostOrderJson(orderJsonWithoutFields);

        Assert.Equal(HttpStatusCode.BadRequest, emptyOrderHttpResponse.StatusCode);
        var fieldErrors = (await ReadJson(emptyOrderHttpResponse)).GetProperty("errors").EnumerateArray()
            .Select(fieldError => (fieldError.GetProperty("field").GetString(), fieldError.GetProperty("message").GetString()))
            .ToList();
        Assert.Equal(
            [
                (OrderFields.OrderSymbolFieldName, OrderMessages.OrderSymbolRequiredMessage),
                (OrderFields.OrderSideFieldName, OrderMessages.OrderSideRequiredMessage),
                (OrderFields.OrderQuantityFieldName, OrderMessages.OrderQuantityRequiredMessage),
                (OrderFields.OrderPriceFieldName, OrderMessages.OrderPriceRequiredMessage)
            ],
            fieldErrors);
        await AssertOnlySentinelReachedAcceptor();
    }

    [Theory]
    [InlineData("buy", '1')]
    [InlineData("sell", '2')]
    public async Task Compra_e_venda_saem_como_NewOrderSingle_e_voltam_aceitas(string jsonSide, char fixSide)
    {
        // TransactTime vai em UTC com milissegundos; a janela aceita esse arredondamento.
        var sentAfter = DateTime.UtcNow.AddMilliseconds(-1);
        var orderHttpResponse = await PostOrderJson($$"""{"symbol":"VALE3","side":"{{jsonSide}}","quantity":250,"price":61.37}""");
        var sentBefore = DateTime.UtcNow;

        Assert.Equal(HttpStatusCode.OK, orderHttpResponse.StatusCode);
        var sentNewOrderSingle = Assert.Single(_generator.Acceptor.ReceivedOrders);
        Assert.Equal("D", sentNewOrderSingle.Header.GetString(Tags.MsgType));
        Assert.Equal("VALE3", sentNewOrderSingle.GetString(Tags.Symbol));
        Assert.Equal(fixSide, sentNewOrderSingle.GetChar(Tags.Side));
        Assert.Equal(250m, sentNewOrderSingle.GetDecimal(Tags.OrderQty));
        Assert.Equal(61.37m, sentNewOrderSingle.GetDecimal(Tags.Price));
        Assert.Equal(OrdType.LIMIT, sentNewOrderSingle.GetChar(Tags.OrdType));
        Assert.InRange(sentNewOrderSingle.GetDateTime(Tags.TransactTime), sentAfter, sentBefore);

        var orderResponse = await ReadJson(orderHttpResponse);
        var clOrdId = sentNewOrderSingle.GetString(Tags.ClOrdID);
        Assert.Equal("accepted", orderResponse.GetProperty("status").GetString());
        Assert.Equal(clOrdId, orderResponse.GetProperty("clOrdId").GetString());
        Assert.Matches("^[0-9a-f]{32}$", clOrdId);
        AssertAnswersItsOwnExecutionReport(orderResponse);
        Assert.Equal("VALE3", orderResponse.GetProperty("symbol").GetString());
        Assert.Equal(jsonSide, orderResponse.GetProperty("side").GetString());
        Assert.Equal(250, orderResponse.GetProperty("quantity").GetInt32());
        Assert.Equal(61.37m, orderResponse.GetProperty("price").GetDecimal());
        Assert.Equal("Ordem aceita.", orderResponse.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("1", "0.01")]
    [InlineData("99999", "999.99")]
    [InlineData("\"1\"", "\"0.01\"")]
    public async Task Bordas_aceitas_de_quantidade_e_preco_saem_pelo_FIX(string quantityJson, string priceJson)
    {
        var orderHttpResponse = await PostOrderJson($$"""{"symbol":"VIIA4","side":"sell","quantity":{{quantityJson}},"price":{{priceJson}}}""");

        var expectedQuantity = int.Parse(quantityJson.Trim('"'));
        var expectedPrice = decimal.Parse(priceJson.Trim('"'), System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(HttpStatusCode.OK, orderHttpResponse.StatusCode);
        var sentNewOrderSingle = Assert.Single(_generator.Acceptor.ReceivedOrders);
        Assert.Equal("VIIA4", sentNewOrderSingle.GetString(Tags.Symbol));
        Assert.Equal(Side.SELL, sentNewOrderSingle.GetChar(Tags.Side));
        Assert.Equal(expectedQuantity, sentNewOrderSingle.GetDecimal(Tags.OrderQty));
        Assert.Equal(expectedPrice, sentNewOrderSingle.GetDecimal(Tags.Price));
        var orderResponse = await ReadJson(orderHttpResponse);
        Assert.Equal("accepted", orderResponse.GetProperty("status").GetString());
        Assert.Equal(expectedQuantity, orderResponse.GetProperty("quantity").GetInt32());
        Assert.Equal(expectedPrice, orderResponse.GetProperty("price").GetDecimal());
    }

    [Fact]
    public async Task Ordem_rejeitada_devolve_o_texto_da_tag_58()
    {
        const string rejectionText = "Ordem rejeitada: a exposição de PETR4 passaria do limite de 100.000.000,00.";
        _generator.Acceptor.ExecutionReportResponder = receivedOrder => _generator.Acceptor.BuildRejectedReport(receivedOrder, rejectionText);

        var orderHttpResponse = await PostOrderJson("""{"symbol":"PETR4","side":"buy","quantity":99999,"price":999.99}""");

        Assert.Equal(HttpStatusCode.OK, orderHttpResponse.StatusCode);
        var rejectedResponse = await ReadJson(orderHttpResponse);
        var clOrdId = Assert.Single(_generator.Acceptor.ReceivedOrders).GetString(Tags.ClOrdID);
        Assert.Equal("rejected", rejectedResponse.GetProperty("status").GetString());
        Assert.Equal(rejectionText, rejectedResponse.GetProperty("message").GetString());
        Assert.Equal(clOrdId, rejectedResponse.GetProperty("clOrdId").GetString());
        AssertAnswersItsOwnExecutionReport(rejectedResponse);
        Assert.Equal("PETR4", rejectedResponse.GetProperty("symbol").GetString());
        Assert.Equal("buy", rejectedResponse.GetProperty("side").GetString());
        Assert.Equal(99999, rejectedResponse.GetProperty("quantity").GetInt32());
        Assert.Equal(999.99m, rejectedResponse.GetProperty("price").GetDecimal());
    }

    [Fact]
    public async Task Ordem_rejeitada_sem_tag_58_devolve_a_mensagem_padrao()
    {
        _generator.Acceptor.ExecutionReportResponder = receivedOrder =>
            _generator.Acceptor.BuildExecutionReport(receivedOrder, ExecType.REJECTED, OrdStatus.REJECTED, 0);

        var orderHttpResponse = await PostOrderJson("""{"symbol":"VALE3","side":"sell","quantity":4,"price":12.34}""");

        Assert.Equal(HttpStatusCode.OK, orderHttpResponse.StatusCode);
        var rejectedResponse = await ReadJson(orderHttpResponse);
        Assert.Equal("rejected", rejectedResponse.GetProperty("status").GetString());
        Assert.Equal("Ordem rejeitada.", rejectedResponse.GetProperty("message").GetString());
        AssertAnswersItsOwnExecutionReport(rejectedResponse);
    }

    [Fact]
    public async Task ExecutionReport_com_ExecType_fora_do_contrato_responde_500()
    {
        // 150=2 (Fill) não está no contrato v1, que só prevê 0 (New) e 8 (Rejected).
        _generator.Acceptor.ExecutionReportResponder = receivedOrder =>
            _generator.Acceptor.BuildExecutionReport(receivedOrder, ExecType.FILL, OrdStatus.FILLED, 0);

        var orderHttpResponse = await PostOrderJson("""{"symbol":"PETR4","side":"buy","quantity":10,"price":20.00}""");

        Assert.Equal(HttpStatusCode.InternalServerError, orderHttpResponse.StatusCode);
        var errorResponse = await ReadJson(orderHttpResponse);
        Assert.Equal("error", errorResponse.GetProperty("status").GetString());
        Assert.Equal("Erro inesperado ao processar a ordem.", errorResponse.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Relatorio_de_outro_ClOrdID_nao_responde_pela_ordem()
    {
        _generator.Acceptor.StrayExecutionReport = receivedOrder =>
            _generator.Acceptor.BuildExecutionReport(receivedOrder, ExecType.REJECTED, OrdStatus.REJECTED, 0, clOrdId: "nao-e-desta-ordem");

        var orderResponse = await ReadJson(await PostOrderJson("""{"symbol":"VIIA4","side":"buy","quantity":5,"price":3.21}"""));

        Assert.Equal("accepted", orderResponse.GetProperty("status").GetString());
        AssertAnswersItsOwnExecutionReport(orderResponse);
        var strayOrderId = _generator.Acceptor.SentExecutionReports["nao-e-desta-ordem"].GetString(Tags.OrderID);
        Assert.NotEqual(strayOrderId, orderResponse.GetProperty("orderId").GetString());
    }

    [Fact]
    public async Task Ordens_simultaneas_recebem_cada_uma_a_sua_resposta()
    {
        // A de PETR4 é respondida depois da de VALE3: as respostas chegam fora da ordem de envio.
        const string rejectionText = "Ordem rejeitada: a exposição de VALE3 passaria do limite de 100.000.000,00.";
        _generator.Acceptor.ExecutionReportResponder = receivedOrder => receivedOrder.GetString(Tags.Symbol) == "VALE3"
            ? _generator.Acceptor.BuildRejectedReport(receivedOrder, rejectionText)
            : _generator.Acceptor.BuildAcceptedReport(receivedOrder);
        _generator.Acceptor.ExecutionReportDelay = receivedOrder =>
            receivedOrder.GetString(Tags.Symbol) == "PETR4" ? TimeSpan.FromMilliseconds(500) : TimeSpan.Zero;

        var petr4Request = PostOrderJson("""{"symbol":"PETR4","side":"buy","quantity":10,"price":30.00}""");
        var vale3Request = PostOrderJson("""{"symbol":"VALE3","side":"sell","quantity":20,"price":60.00}""");
        await Task.WhenAll(petr4Request, vale3Request);

        var petr4Response = await ReadJson(await petr4Request);
        var vale3Response = await ReadJson(await vale3Request);
        Assert.Equal("accepted", petr4Response.GetProperty("status").GetString());
        Assert.Equal("PETR4", petr4Response.GetProperty("symbol").GetString());
        Assert.Equal("rejected", vale3Response.GetProperty("status").GetString());
        Assert.Equal(rejectionText, vale3Response.GetProperty("message").GetString());
        AssertAnswersItsOwnExecutionReport(petr4Response);
        AssertAnswersItsOwnExecutionReport(vale3Response);
    }

    [Fact]
    public async Task Resposta_que_chega_depois_dos_5_segundos_e_descartada()
    {
        _generator.Acceptor.ExecutionReportDelay = receivedOrder => TimeSpan.FromSeconds(6);

        var lateOrderHttpResponse = await PostOrderJson("""{"symbol":"PETR4","side":"buy","quantity":10,"price":30.00}""");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, lateOrderHttpResponse.StatusCode);
        var lateClOrdId = Assert.Single(_generator.Acceptor.ReceivedOrders).GetString(Tags.ClOrdID);
        await TestHost.WaitUntil(() => _generator.Acceptor.SentExecutionReports.ContainsKey(lateClOrdId));

        _generator.Acceptor.ExecutionReportDelay = receivedOrder => TimeSpan.Zero;
        var nextOrderResponse = await ReadJson(await PostOrderJson("""{"symbol":"VALE3","side":"buy","quantity":10,"price":30.00}"""));

        Assert.Equal("accepted", nextOrderResponse.GetProperty("status").GetString());
        AssertAnswersItsOwnExecutionReport(nextOrderResponse);
        Assert.Equal(0, _generator.Factory.Services.GetRequiredService<FixOrderClient>().OrdersAwaitingExecutionReportCount);
    }

    [Fact]
    public async Task Mensagens_FIX_de_ida_e_volta_aparecem_no_stdout()
    {
        // Contrato §3: o log FIX vai para o stdout, que é o que o docker compose logs mostra.
        var originalStdout = Console.Out;
        var capturedStdout = new StringWriter();
        Console.SetOut(TextWriter.Synchronized(capturedStdout));
        string clOrdId;
        try
        {
            var orderResponse = await ReadJson(await PostOrderJson("""{"symbol":"VALE3","side":"buy","quantity":3,"price":45.10}"""));
            clOrdId = orderResponse.GetProperty("clOrdId").GetString()!;
        }
        finally
        {
            Console.SetOut(originalStdout);
        }

        // O ScreenLog do QuickFIX troca o separador SOH por "|" e marca a direção da mensagem.
        var stdoutLines = capturedStdout.ToString().Split('\n');
        Assert.Single(stdoutLines, stdoutLine => stdoutLine.StartsWith("<outgoing> ") && stdoutLine.Contains("|35=D|") && stdoutLine.Contains("|11=" + clOrdId + "|"));
        Assert.Single(stdoutLines, stdoutLine => stdoutLine.StartsWith("<incoming> ") && stdoutLine.Contains("|35=8|") && stdoutLine.Contains("|11=" + clOrdId + "|"));
    }

    // A sessão FIX entrega em ordem: se uma ordem inválida tivesse saído, ela chegaria antes da sentinela.
    private async Task AssertOnlySentinelReachedAcceptor()
    {
        var sentinelResponse = await ReadJson(await PostOrderJson(SentinelOrderJson));
        var sentinelClOrdId = sentinelResponse.GetProperty("clOrdId").GetString();
        var receivedOrder = Assert.Single(_generator.Acceptor.ReceivedOrders);
        Assert.Equal(sentinelClOrdId, receivedOrder.GetString(Tags.ClOrdID));
    }

    private void AssertAnswersItsOwnExecutionReport(JsonElement orderResponse)
    {
        var executionReport = _generator.Acceptor.SentExecutionReports[orderResponse.GetProperty("clOrdId").GetString()!];
        Assert.Equal(executionReport.GetString(Tags.OrderID), orderResponse.GetProperty("orderId").GetString());
        Assert.Equal(executionReport.GetString(Tags.ExecID), orderResponse.GetProperty("execId").GetString());
    }

    private Task<HttpResponseMessage> PostOrderJson(string orderJson) =>
        _generator.Client.PostAsync("/api/orders", new StringContent(orderJson, Encoding.UTF8, "application/json"));

    internal static async Task<JsonElement> ReadJson(HttpResponseMessage httpResponse) =>
        JsonDocument.Parse(await httpResponse.Content.ReadAsStringAsync()).RootElement;
}

// CA-19: sem a outra ponta, ou com ela muda, a API responde em português dentro do prazo e não segura nada.
public sealed class OrderCommunicationTests
{
    private const string ValidOrderJson = """{"symbol":"PETR4","side":"buy","quantity":100,"price":10.50}""";
    private const string CommunicationMessage = "Não foi possível falar com o OrderAccumulator. Tente de novo em instantes.";

    [Fact]
    public async Task Sem_sessao_FIX_responde_503_na_hora()
    {
        await using var orderGenerator = TestHost.CreateOrderGeneratorFactory(TestHost.FreePort());
        using var orderGeneratorClient = orderGenerator.CreateClient();

        var responseClock = Stopwatch.StartNew();
        var orderHttpResponse = await PostValidOrder(orderGeneratorClient);
        responseClock.Stop();

        await AssertCommunicationError(orderHttpResponse);
        Assert.True(responseClock.Elapsed < TimeSpan.FromSeconds(1), $"levou {responseClock.Elapsed}");
        Assert.Equal(0, orderGenerator.Services.GetRequiredService<FixOrderClient>().OrdersAwaitingExecutionReportCount);
    }

    [Fact]
    public void Acceptor_de_teste_escuta_so_no_loopback()
    {
        // Escutando em todas as redes, o Windows pede ao dono para liberar o testhost no firewall.
        using var acceptor = new TestAcceptor(TestHost.FreePort());
        acceptor.Start();

        var acceptorListeners = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners().Where(listenerEndpoint => listenerEndpoint.Port == acceptor.Port).ToList();

        var acceptorListener = Assert.Single(acceptorListeners);
        Assert.Equal(IPAddress.Loopback, acceptorListener.Address);
    }

    [Fact]
    public async Task Acceptor_mudo_responde_503_em_5_segundos_e_descarta_a_espera()
    {
        using var acceptor = new TestAcceptor(TestHost.FreePort());
        acceptor.Start();
        await using var orderGenerator = TestHost.CreateOrderGeneratorFactory(acceptor.Port);
        using var orderGeneratorClient = orderGenerator.CreateClient();
        await acceptor.WaitForLogonAsync();

        var responseClock = Stopwatch.StartNew();
        var orderHttpResponse = await PostValidOrder(orderGeneratorClient);
        responseClock.Stop();

        await AssertCommunicationError(orderHttpResponse);
        Assert.Single(acceptor.ReceivedOrders);
        Assert.InRange(responseClock.Elapsed, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(6.5));
        Assert.Equal(0, orderGenerator.Services.GetRequiredService<FixOrderClient>().OrdersAwaitingExecutionReportCount);
    }

    [Fact]
    public async Task Quando_o_acceptor_volta_o_initiator_reloga_e_so_a_ordem_nova_passa()
    {
        using var acceptor = new TestAcceptor(TestHost.FreePort());
        await using var orderGenerator = TestHost.CreateOrderGeneratorFactory(acceptor.Port);
        using var orderGeneratorClient = orderGenerator.CreateClient();

        await AssertCommunicationError(await PostValidOrder(orderGeneratorClient));

        acceptor.ExecutionReportResponder = acceptor.BuildAcceptedReport;
        acceptor.Start();
        await acceptor.WaitForLogonAsync();

        var orderHttpResponse = await PostValidOrder(orderGeneratorClient);
        Assert.Equal(HttpStatusCode.OK, orderHttpResponse.StatusCode);
        var orderResponse = await OrderApiTests.ReadJson(orderHttpResponse);
        Assert.Equal("accepted", orderResponse.GetProperty("status").GetString());
        // D-34: a ordem recusada sem sessão não ficou na store para sair depois do logon.
        var receivedOrder = Assert.Single(acceptor.ReceivedOrders);
        Assert.Equal(orderResponse.GetProperty("clOrdId").GetString(), receivedOrder.GetString(Tags.ClOrdID));
    }

    private static Task<HttpResponseMessage> PostValidOrder(HttpClient orderGeneratorClient) =>
        orderGeneratorClient.PostAsync("/api/orders", new StringContent(ValidOrderJson, Encoding.UTF8, "application/json"));

    private static async Task AssertCommunicationError(HttpResponseMessage orderHttpResponse)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, orderHttpResponse.StatusCode);
        var communicationErrorResponse = await OrderApiTests.ReadJson(orderHttpResponse);
        Assert.Equal("communication_error", communicationErrorResponse.GetProperty("status").GetString());
        Assert.Equal(CommunicationMessage, communicationErrorResponse.GetProperty("message").GetString());
    }
}
