using System.Net;
using System.Text.Json;

namespace OrderAccumulator.Tests;

// CA-17 (parte da F3): o GET /api/exposures no formato do contrato, antes e depois de ordens pelo FIX.
[Collection(PostgresCollection.Name)]
public sealed class ExposuresEndpointTests(PostgresFixture db) : IAsyncLifetime
{
    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Get_returns_the_three_symbols_in_contract_order_with_limit_and_remaining()
    {
        await using var app = new AccumulatorApp(db.ConnectionString).Start();

        var body = await ReadJsonAsync(app);

        Assert.Equal(100_000_000m, body.GetProperty("limit").GetDecimal());
        Assert.Equal(
            [("PETR4", 0m, 100_000_000m), ("VALE3", 0m, 100_000_000m), ("VIIA4", 0m, 100_000_000m)],
            Items(body));
    }

    [Fact]
    public async Task Accepted_orders_move_the_values_and_a_rejected_one_does_not()
    {
        await using var app = new AccumulatorApp(db.ConnectionString).Start();
        using var fix = await TestInitiator.ConnectAsync(app.FixPort);

        await fix.SendAsync(TestInitiator.Order("compra-petr4", "PETR4", '1', 100, 10.50m));
        await fix.SendAsync(TestInitiator.Order("venda-vale3", "VALE3", '2', 20, 25.00m));
        var afterAccepted = Items(await ReadJsonAsync(app));

        var rejected = await fix.SendAsync(TestInitiator.Order("rejeitada-viia4", "VIIA4", '1', 100_000, 1.00m));
        var afterRejected = Items(await ReadJsonAsync(app));

        Assert.Equal(
            [("PETR4", 1_050.00m, 99_998_950.00m), ("VALE3", -500.00m, 99_999_500.00m), ("VIIA4", 0m, 100_000_000m)],
            afterAccepted);
        Assert.Equal(QuickFix.Fields.ExecType.REJECTED, rejected.ExecType.Value);
        Assert.Equal(afterAccepted, afterRejected);
    }

    private static async Task<JsonElement> ReadJsonAsync(AccumulatorApp app)
    {
        var response = await app.CreateClient().GetAsync("/api/exposures");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    // Lê pelos nomes do contrato (camelCase), não pelo tipo C#: um nome trocado aqui quebra o teste.
    private static List<(string Symbol, decimal Exposure, decimal Remaining)> Items(JsonElement body) =>
        body.GetProperty("exposures").EnumerateArray()
            .Select(item => (
                item.GetProperty("symbol").GetString()!,
                item.GetProperty("exposure").GetDecimal(),
                item.GetProperty("remaining").GetDecimal()))
            .ToList();
}
