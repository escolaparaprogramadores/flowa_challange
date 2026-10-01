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
        _generator.Acceptor.Responder = _generator.Acceptor.Accept;
        _generator.Acceptor.ReceivedOrders.Clear();
    }

    public static TheoryData<string, string, string> InvalidFields => new()
    {
        // CA-1
        { """{"symbol":"XPTO3","side":"buy","quantity":100,"price":10.50}""", OrderFields.Symbol, OrderMessages.SymbolInvalid },
        { """{"symbol":"petr4","side":"buy","quantity":100,"price":10.50}""", OrderFields.Symbol, OrderMessages.SymbolInvalid },
        { """{"side":"buy","quantity":100,"price":10.50}""", OrderFields.Symbol, OrderMessages.SymbolRequired },
        // CA-2
        { """{"symbol":"PETR4","side":"compra","quantity":100,"price":10.50}""", OrderFields.Side, OrderMessages.SideInvalid },
        { """{"symbol":"PETR4","side":"BUY","quantity":100,"price":10.50}""", OrderFields.Side, OrderMessages.SideInvalid },
        { """{"symbol":"PETR4","quantity":100,"price":10.50}""", OrderFields.Side, OrderMessages.SideRequired },
        // CA-3
        { """{"symbol":"PETR4","side":"buy","quantity":0,"price":10.50}""", OrderFields.Quantity, OrderMessages.QuantityNotPositive },
        { """{"symbol":"PETR4","side":"buy","quantity":-1,"price":10.50}""", OrderFields.Quantity, OrderMessages.QuantityNotPositive },
        { """{"symbol":"PETR4","side":"buy","quantity":1.5,"price":10.50}""", OrderFields.Quantity, OrderMessages.QuantityNotInteger },
        { """{"symbol":"PETR4","side":"buy","quantity":"abc","price":10.50}""", OrderFields.Quantity, OrderMessages.QuantityNotInteger },
        { """{"symbol":"PETR4","side":"buy","quantity":100000,"price":10.50}""", OrderFields.Quantity, OrderMessages.QuantityTooLarge },
        // CA-4
        { """{"symbol":"PETR4","side":"buy","quantity":100,"price":0}""", OrderFields.Price, OrderMessages.PriceNotPositive },
        { """{"symbol":"PETR4","side":"buy","quantity":100,"price":-1}""", OrderFields.Price, OrderMessages.PriceNotPositive },
        { """{"symbol":"PETR4","side":"buy","quantity":100,"price":1000}""", OrderFields.Price, OrderMessages.PriceTooLarge },
        { """{"symbol":"PETR4","side":"buy","quantity":100,"price":10.005}""", OrderFields.Price, OrderMessages.PriceOffTick },
        { """{"symbol":"PETR4","side":"buy","quantity":100,"price":"abc"}""", OrderFields.Price, OrderMessages.PriceNotNumber },
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
                (OrderFields.Symbol, OrderMessages.SymbolRequired),
                (OrderFields.Side, OrderMessages.SideRequired),
                (OrderFields.Quantity, OrderMessages.QuantityRequired),
                (OrderFields.Price, OrderMessages.PriceRequired)
            ],
            errors);
        Assert.Empty(_generator.Acceptor.ReceivedOrders);
    }

    [Theory]
    [InlineData("buy", '1')]
    [InlineData("sell", '2')]
    public async Task Compra_e_venda_saem_como_NewOrderSingle_e_voltam_aceitas(string side, char fixSide)
    {
        var response = await PostRaw($$"""{"symbol":"VALE3","side":"{{side}}","quantity":250,"price":61.37}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = Assert.Single(_generator.Acceptor.ReceivedOrders);
        Assert.Equal("D", sent.Header.GetString(Tags.MsgType));
        Assert.Equal("VALE3", sent.GetString(Tags.Symbol));
        Assert.Equal(fixSide, sent.GetChar(Tags.Side));
        Assert.Equal(250m, sent.GetDecimal(Tags.OrderQty));
        Assert.Equal(61.37m, sent.GetDecimal(Tags.Price));
        Assert.Equal(OrdType.LIMIT, sent.GetChar(Tags.OrdType));
        Assert.True(sent.IsSetField(Tags.TransactTime));

        var json = await ReadJson(response);
        Assert.Equal("accepted", json.GetProperty("status").GetString());
        Assert.Equal(sent.GetString(Tags.ClOrdID), json.GetProperty("clOrdId").GetString());
        Assert.Matches("^[0-9a-f]{32}$", json.GetProperty("clOrdId").GetString());
        Assert.StartsWith("ORD-", json.GetProperty("orderId").GetString());
        Assert.StartsWith("EXE-", json.GetProperty("execId").GetString());
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

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = Assert.Single(_generator.Acceptor.ReceivedOrders);
        Assert.Equal(decimal.Parse(quantity.Trim('"')), sent.GetDecimal(Tags.OrderQty));
        Assert.Equal(decimal.Parse(price.Trim('"'), System.Globalization.CultureInfo.InvariantCulture), sent.GetDecimal(Tags.Price));
        Assert.Equal("accepted", (await ReadJson(response)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Ordem_rejeitada_devolve_o_texto_da_tag_58()
    {
        const string motivo = "Ordem rejeitada: a exposição de PETR4 passaria do limite de 100.000.000,00.";
        _generator.Acceptor.Responder = order => _generator.Acceptor.Reject(order, motivo);

        var response = await PostRaw("""{"symbol":"PETR4","side":"buy","quantity":99999,"price":999.99}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ReadJson(response);
        Assert.Equal("rejected", json.GetProperty("status").GetString());
        Assert.Equal(motivo, json.GetProperty("message").GetString());
        Assert.Equal("PETR4", json.GetProperty("symbol").GetString());
        Assert.Equal("buy", json.GetProperty("side").GetString());
        Assert.Equal(99999, json.GetProperty("quantity").GetInt32());
        Assert.Equal(999.99m, json.GetProperty("price").GetDecimal());
        Assert.Single(_generator.Acceptor.ReceivedOrders);
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
