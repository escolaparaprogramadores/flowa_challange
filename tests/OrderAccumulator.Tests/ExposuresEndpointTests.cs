using System.Net;
using System.Text.Json;

namespace OrderAccumulator.Tests;

// CA-17 (parte da F3): o GET /api/exposures no formato do contrato, antes e depois de ordens pelo FIX.
[Collection(OrderAccumulatorPostgresCollection.Name)]
public sealed class ExposuresEndpointTests(OrderAccumulatorPostgresFixture orderAccumulatorDatabase) : IAsyncLifetime
{
    public Task InitializeAsync() => orderAccumulatorDatabase.ResetOrdersAndExposuresAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Get_returns_the_three_symbols_in_contract_order_with_limit_and_remaining()
    {
        await using var app = new AccumulatorApp(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();

        var exposuresJson = await GetExposuresJsonAsync(app);

        Assert.Equal(100_000_000m, exposuresJson.GetProperty("limit").GetDecimal());
        Assert.Equal(
            [("PETR4", 0m, 100_000_000m), ("VALE3", 0m, 100_000_000m), ("VIIA4", 0m, 100_000_000m)],
            ReadExposureEntries(exposuresJson));
    }

    [Fact]
    public async Task Accepted_orders_move_the_values_and_a_rejected_one_does_not()
    {
        await using var app = new AccumulatorApp(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();
        using var testInitiator = await TestInitiator.LogOnToAcceptorAsync(app.FixPort);

        await testInitiator.SendExpectingExecutionReportAsync(TestInitiator.NewOrder("compra-petr4", "PETR4", '1', 100, 10.50m));
        await testInitiator.SendExpectingExecutionReportAsync(TestInitiator.NewOrder("venda-vale3", "VALE3", '2', 20, 25.00m));
        var exposuresAfterAccepted = ReadExposureEntries(await GetExposuresJsonAsync(app));

        var rejectedReport = await testInitiator.SendExpectingExecutionReportAsync(TestInitiator.NewOrder("rejeitada-viia4", "VIIA4", '1', 100_000, 1.00m));
        var exposuresAfterRejected = ReadExposureEntries(await GetExposuresJsonAsync(app));

        Assert.Equal(
            [("PETR4", 1_050.00m, 99_998_950.00m), ("VALE3", -500.00m, 99_999_500.00m), ("VIIA4", 0m, 100_000_000m)],
            exposuresAfterAccepted);
        Assert.Equal(QuickFix.Fields.ExecType.REJECTED, rejectedReport.ExecType.Value);
        Assert.Equal("A quantidade deve ser menor que 100.000.", rejectedReport.Text.Value);
        Assert.Equal(exposuresAfterAccepted, exposuresAfterRejected);
    }

    private static async Task<JsonElement> GetExposuresJsonAsync(AccumulatorApp app)
    {
        var exposuresHttpResponse = await app.CreateClient().GetAsync("/api/exposures");
        Assert.Equal(HttpStatusCode.OK, exposuresHttpResponse.StatusCode);
        Assert.Equal("application/json", exposuresHttpResponse.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(await exposuresHttpResponse.Content.ReadAsStringAsync()).RootElement;
    }

    // Lê pelos nomes do contrato (camelCase), não pelo tipo C#: um nome trocado aqui quebra o teste.
    private static List<(string Symbol, decimal Exposure, decimal Remaining)> ReadExposureEntries(JsonElement exposuresJson) =>
        exposuresJson.GetProperty("exposures").EnumerateArray()
            .Select(exposureEntry => (
                exposureEntry.GetProperty("symbol").GetString()!,
                exposureEntry.GetProperty("exposure").GetDecimal(),
                exposureEntry.GetProperty("remaining").GetDecimal()))
            .ToList();
}
