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
        Factory = TestHost.Generator(Acceptor.Port);
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
    private readonly LoggedOnGenerator _generator;

    public OrderApiTests(LoggedOnGenerator generator)
    {
        _generator = generator;
        _generator.Acceptor.Reset();
    }

    public static TheoryData<string, string, string> InvalidFields => new()
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
    [MemberData(nameof(InvalidFields))]
    public async Task Campo_invalido_responde_400_em_portugues_e_nao_envia_FIX(string body, string field, string message)
    {
        var response = await PostRaw(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await ReadJson(response);
        Assert.Equal("validation_error", json.GetProperty("status").GetString());
        Assert.Equal("A ordem tem campos inválidos.", json.GetProperty("message").GetString());
        var error = Assert.Single(json.GetProperty("errors").EnumerateArray());
        Assert.Equal(field, error.GetProperty("field").GetString());
        Assert.Equal(message, error.GetProperty("message").GetString());
        Assert.Empty(_generator.Acceptor.ReceivedOrders);
    }

    [Fact]
    public async Task Corpo_que_nao_e_JSON_responde_400_com_os_quatro_campos_obrigatorios()
    {
        var response = await PostRaw("isto não é json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await ReadJson(response)).GetProperty("errors").EnumerateArray()
            .Select(e => (e.GetProperty("field").GetString(), e.GetProperty("message").GetString()))
            .ToList();
        Assert.Equal(
            [
                (OrderFields.OrderSymbolFieldName, OrderMessages.OrderSymbolRequiredMessage),
                (OrderFields.OrderSideFieldName, OrderMessages.OrderSideRequiredMessage),
                (OrderFields.OrderQuantityFieldName, OrderMessages.OrderQuantityRequiredMessage),
                (OrderFields.OrderPriceFieldName, OrderMessages.OrderPriceRequiredMessage)
            ],
            errors);
        Assert.Empty(_generator.Acceptor.ReceivedOrders);
    }

    [Theory]
    [InlineData("buy", '1')]
    [InlineData("sell", '2')]
    public async Task Compra_e_venda_saem_como_NewOrderSingle_e_voltam_aceitas(string side, char fixSide)
    {
        // TransactTime vai em UTC com milissegundos; a janela aceita esse arredondamento.
        var before = DateTime.UtcNow.AddMilliseconds(-1);
        var response = await PostRaw($$"""{"symbol":"VALE3","side":"{{side}}","quantity":250,"price":61.37}""");
        var after = DateTime.UtcNow;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = Assert.Single(_generator.Acceptor.ReceivedOrders);
        Assert.Equal("D", sent.Header.GetString(Tags.MsgType));
        Assert.Equal("VALE3", sent.GetString(Tags.Symbol));
        Assert.Equal(fixSide, sent.GetChar(Tags.Side));
        Assert.Equal(250m, sent.GetDecimal(Tags.OrderQty));
        Assert.Equal(61.37m, sent.GetDecimal(Tags.Price));
        Assert.Equal(OrdType.LIMIT, sent.GetChar(Tags.OrdType));
        Assert.InRange(sent.GetDateTime(Tags.TransactTime), before, after);

        var json = await ReadJson(response);
        var clOrdId = sent.GetString(Tags.ClOrdID);
        var report = _generator.Acceptor.SentReports[clOrdId];
        Assert.Equal("accepted", json.GetProperty("status").GetString());
        Assert.Equal(clOrdId, json.GetProperty("clOrdId").GetString());
        Assert.Matches("^[0-9a-f]{32}$", clOrdId);
        Assert.Equal(report.GetString(Tags.OrderID), json.GetProperty("orderId").GetString());
        Assert.Equal(report.GetString(Tags.ExecID), json.GetProperty("execId").GetString());
        Assert.Equal("VALE3", json.GetProperty("symbol").GetString());
        Assert.Equal(side, json.GetProperty("side").GetString());
        Assert.Equal(250, json.GetProperty("quantity").GetInt32());
        Assert.Equal(61.37m, json.GetProperty("price").GetDecimal());
        Assert.Equal("Ordem aceita.", json.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("1", "0.01")]
    [InlineData("99999", "999.99")]
    [InlineData("\"1\"", "\"0.01\"")]
    public async Task Bordas_aceitas_de_quantidade_e_preco_saem_pelo_FIX(string quantity, string price)
    {
        var response = await PostRaw($$"""{"symbol":"VIIA4","side":"sell","quantity":{{quantity}},"price":{{price}}}""");

        var expectedQuantity = int.Parse(quantity.Trim('"'));
        var expectedPrice = decimal.Parse(price.Trim('"'), System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = Assert.Single(_generator.Acceptor.ReceivedOrders);
        Assert.Equal(expectedQuantity, sent.GetDecimal(Tags.OrderQty));
        Assert.Equal(expectedPrice, sent.GetDecimal(Tags.Price));
        var json = await ReadJson(response);
        Assert.Equal("accepted", json.GetProperty("status").GetString());
        Assert.Equal(expectedQuantity, json.GetProperty("quantity").GetInt32());
        Assert.Equal(expectedPrice, json.GetProperty("price").GetDecimal());
    }

    [Fact]
    public async Task Ordem_rejeitada_devolve_o_texto_da_tag_58()
    {
        const string motivo = "Ordem rejeitada: a exposição de PETR4 passaria do limite de 100.000.000,00.";
        _generator.Acceptor.Responder = order => _generator.Acceptor.Reject(order, motivo);

        var response = await PostRaw("""{"symbol":"PETR4","side":"buy","quantity":99999,"price":999.99}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ReadJson(response);
        var clOrdId = Assert.Single(_generator.Acceptor.ReceivedOrders).GetString(Tags.ClOrdID);
        var report = _generator.Acceptor.SentReports[clOrdId];
        Assert.Equal("rejected", json.GetProperty("status").GetString());
        Assert.Equal(motivo, json.GetProperty("message").GetString());
        Assert.Equal(clOrdId, json.GetProperty("clOrdId").GetString());
        Assert.Equal(report.GetString(Tags.OrderID), json.GetProperty("orderId").GetString());
        Assert.Equal(report.GetString(Tags.ExecID), json.GetProperty("execId").GetString());
        Assert.Equal("PETR4", json.GetProperty("symbol").GetString());
        Assert.Equal("buy", json.GetProperty("side").GetString());
        Assert.Equal(99999, json.GetProperty("quantity").GetInt32());
        Assert.Equal(999.99m, json.GetProperty("price").GetDecimal());
    }

    [Fact]
    public async Task ExecutionReport_com_ExecType_fora_do_contrato_responde_500()
    {
        // 150=2 (Fill) não está no contrato v1, que só prevê 0 (New) e 8 (Rejected).
        _generator.Acceptor.Responder = order => _generator.Acceptor.Report(order, ExecType.FILL, OrdStatus.FILLED, 0);

        var response = await PostRaw("""{"symbol":"PETR4","side":"buy","quantity":10,"price":20.00}""");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var json = await ReadJson(response);
        Assert.Equal("error", json.GetProperty("status").GetString());
        Assert.Equal("Erro inesperado ao processar a ordem.", json.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Relatorio_de_outro_ClOrdID_nao_responde_pela_ordem()
    {
        _generator.Acceptor.StrayReport = order =>
            _generator.Acceptor.Report(order, ExecType.REJECTED, OrdStatus.REJECTED, 0, clOrdId: "nao-e-desta-ordem");

        var response = await PostRaw("""{"symbol":"VIIA4","side":"buy","quantity":5,"price":3.21}""");

        var json = await ReadJson(response);
        var clOrdId = Assert.Single(_generator.Acceptor.ReceivedOrders).GetString(Tags.ClOrdID);
        Assert.Equal("accepted", json.GetProperty("status").GetString());
        Assert.Equal(_generator.Acceptor.SentReports[clOrdId].GetString(Tags.OrderID), json.GetProperty("orderId").GetString());
        Assert.NotEqual(_generator.Acceptor.SentReports["nao-e-desta-ordem"].GetString(Tags.OrderID), json.GetProperty("orderId").GetString());
    }

    [Fact]
    public async Task Ordens_simultaneas_recebem_cada_uma_a_sua_resposta()
    {
        // A de PETR4 é respondida depois da de VALE3: as respostas chegam fora da ordem de envio.
        const string motivo = "Ordem rejeitada: a exposição de VALE3 passaria do limite de 100.000.000,00.";
        _generator.Acceptor.Responder = order => order.GetString(Tags.Symbol) == "VALE3"
            ? _generator.Acceptor.Reject(order, motivo)
            : _generator.Acceptor.Accept(order);
        _generator.Acceptor.ReplyDelay = order =>
            order.GetString(Tags.Symbol) == "PETR4" ? TimeSpan.FromMilliseconds(500) : TimeSpan.Zero;

        var petr4 = PostRaw("""{"symbol":"PETR4","side":"buy","quantity":10,"price":30.00}""");
        var vale3 = PostRaw("""{"symbol":"VALE3","side":"sell","quantity":20,"price":60.00}""");
        await Task.WhenAll(petr4, vale3);

        var petr4Json = await ReadJson(await petr4);
        var vale3Json = await ReadJson(await vale3);
        Assert.Equal("accepted", petr4Json.GetProperty("status").GetString());
        Assert.Equal("PETR4", petr4Json.GetProperty("symbol").GetString());
        Assert.Equal("rejected", vale3Json.GetProperty("status").GetString());
        Assert.Equal(motivo, vale3Json.GetProperty("message").GetString());
        AssertAnswersItsOwnReport(petr4Json);
        AssertAnswersItsOwnReport(vale3Json);
    }

    [Fact]
    public async Task Resposta_que_chega_depois_dos_5_segundos_e_descartada()
    {
        _generator.Acceptor.ReplyDelay = order => TimeSpan.FromSeconds(6);

        var late = await PostRaw("""{"symbol":"PETR4","side":"buy","quantity":10,"price":30.00}""");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, late.StatusCode);
        var lateClOrdId = Assert.Single(_generator.Acceptor.ReceivedOrders).GetString(Tags.ClOrdID);
        await WaitUntil(() => _generator.Acceptor.SentReports.ContainsKey(lateClOrdId));

        _generator.Acceptor.ReplyDelay = order => TimeSpan.Zero;
        var next = await ReadJson(await PostRaw("""{"symbol":"VALE3","side":"buy","quantity":10,"price":30.00}"""));

        Assert.Equal("accepted", next.GetProperty("status").GetString());
        AssertAnswersItsOwnReport(next);
        Assert.Equal(0, _generator.Factory.Services.GetRequiredService<FixOrderClient>().PendingCount);
    }

    private void AssertAnswersItsOwnReport(JsonElement json)
    {
        var report = _generator.Acceptor.SentReports[json.GetProperty("clOrdId").GetString()!];
        Assert.Equal(report.GetString(Tags.OrderID), json.GetProperty("orderId").GetString());
        Assert.Equal(report.GetString(Tags.ExecID), json.GetProperty("execId").GetString());
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "a condição esperada não aconteceu em 10 s");
            await Task.Delay(50);
        }
    }

    private Task<HttpResponseMessage> PostRaw(string body) =>
        _generator.Client.PostAsync("/api/orders", new StringContent(body, Encoding.UTF8, "application/json"));

    internal static async Task<JsonElement> ReadJson(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
}

// CA-19: sem a outra ponta, ou com ela muda, a API responde em português dentro do prazo e não segura nada.
public sealed class OrderCommunicationTests
{
    private const string ValidOrder = """{"symbol":"PETR4","side":"buy","quantity":100,"price":10.50}""";
    private const string CommunicationMessage = "Não foi possível falar com o OrderAccumulator. Tente de novo em instantes.";

    [Fact]
    public async Task Sem_sessao_FIX_responde_503_na_hora()
    {
        await using var factory = TestHost.Generator(TestHost.FreePort());
        using var client = factory.CreateClient();

        var clock = Stopwatch.StartNew();
        var response = await Post(client);
        clock.Stop();

        await AssertCommunicationError(response);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"levou {clock.Elapsed}");
        Assert.Equal(0, factory.Services.GetRequiredService<FixOrderClient>().PendingCount);
    }

    [Fact]
    public void Acceptor_de_teste_escuta_so_no_loopback()
    {
        // Escutando em todas as redes, o Windows pede ao dono para liberar o testhost no firewall.
        using var acceptor = new TestAcceptor(TestHost.FreePort());
        acceptor.Start();

        var listeners = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners().Where(endpoint => endpoint.Port == acceptor.Port).ToList();

        var listener = Assert.Single(listeners);
        Assert.Equal(IPAddress.Loopback, listener.Address);
    }

    [Fact]
    public async Task Acceptor_mudo_responde_503_em_5_segundos_e_descarta_a_espera()
    {
        using var acceptor = new TestAcceptor(TestHost.FreePort());
        acceptor.Start();
        await using var factory = TestHost.Generator(acceptor.Port);
        using var client = factory.CreateClient();
        await acceptor.WaitForLogonAsync();

        var clock = Stopwatch.StartNew();
        var response = await Post(client);
        clock.Stop();

        await AssertCommunicationError(response);
        Assert.Single(acceptor.ReceivedOrders);
        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(6.5));
        Assert.Equal(0, factory.Services.GetRequiredService<FixOrderClient>().PendingCount);
    }

    [Fact]
    public async Task Quando_o_acceptor_volta_o_initiator_reloga_e_a_ordem_passa()
    {
        using var acceptor = new TestAcceptor(TestHost.FreePort());
        await using var factory = TestHost.Generator(acceptor.Port);
        using var client = factory.CreateClient();

        await AssertCommunicationError(await Post(client));

        acceptor.Responder = acceptor.Accept;
        acceptor.Start();
        await acceptor.WaitForLogonAsync();

        var response = await Post(client);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("accepted", (await OrderApiTests.ReadJson(response)).GetProperty("status").GetString());
    }

    private static Task<HttpResponseMessage> Post(HttpClient client) =>
        client.PostAsync("/api/orders", new StringContent(ValidOrder, Encoding.UTF8, "application/json"));

    private static async Task AssertCommunicationError(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var json = await OrderApiTests.ReadJson(response);
        Assert.Equal("communication_error", json.GetProperty("status").GetString());
        Assert.Equal(CommunicationMessage, json.GetProperty("message").GetString());
    }
}
