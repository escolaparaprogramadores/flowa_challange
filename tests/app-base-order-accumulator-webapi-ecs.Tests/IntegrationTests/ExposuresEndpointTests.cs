using System.Net;
using System.Text.Json;

namespace Base.OrderAccumulator.Tests;

// CA-17 (parte da F3): o GET /api/exposures no formato do contrato, antes e depois de ordens pelo FIX.
[Collection(OrderAccumulatorPostgresCollection.Name)]
public sealed class ExposuresEndpointTests(OrderAccumulatorPostgresFixture orderAccumulatorDatabase) : IAsyncLifetime
{
    public Task InitializeAsync() => orderAccumulatorDatabase.ResetOrdersAndExposuresAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Get_exposures_returns_the_three_symbols_in_contract_order_with_limit_and_remaining()
    {
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();

        var exposuresJson = await GetExposuresJsonAsync(orderAccumulatorTestApp);

        Assert.Equal(100_000_000m, exposuresJson.GetProperty("limit").GetDecimal());
        Assert.Equal(
            [("PETR4", 0m, 100_000_000m), ("VALE3", 0m, 100_000_000m), ("VIIA4", 0m, 100_000_000m)],
            ReadExposureEntries(exposuresJson));
    }

    [Fact]
    public async Task Accepted_orders_move_the_exposures_and_a_rejected_one_does_not()
    {
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();
        using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort);

        await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder("compra-petr4", "PETR4", '1', 100, 10.50m));
        await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder("venda-vale3", "VALE3", '2', 20, 25.00m));
        var exposuresAfterAccepted = ReadExposureEntries(await GetExposuresJsonAsync(orderAccumulatorTestApp));

        var rejectedOrderExecutionReport = await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder("rejeitada-viia4", "VIIA4", '1', 100_000, 1.00m));
        var exposuresAfterRejected = ReadExposureEntries(await GetExposuresJsonAsync(orderAccumulatorTestApp));

        Assert.Equal(
            [("PETR4", 1_050.00m, 99_998_950.00m), ("VALE3", -500.00m, 99_999_500.00m), ("VIIA4", 0m, 100_000_000m)],
            exposuresAfterAccepted);
        Assert.Equal(QuickFix.Fields.ExecType.REJECTED, rejectedOrderExecutionReport.ExecType.Value);
        Assert.Equal("A quantidade deve ser menor que 100.000.", rejectedOrderExecutionReport.Text.Value);
        Assert.Equal(exposuresAfterAccepted, exposuresAfterRejected);
    }

    private static async Task<JsonElement> GetExposuresJsonAsync(OrderAccumulatorFixTestHost orderAccumulatorTestApp)
    {
        var exposuresHttpResponse = await orderAccumulatorTestApp.CreateClient().GetAsync("/api/exposures");
        Assert.Equal(HttpStatusCode.OK, exposuresHttpResponse.StatusCode);
        Assert.Equal("application/json", exposuresHttpResponse.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(await exposuresHttpResponse.Content.ReadAsStringAsync()).RootElement;
    }

    // Lê pelos nomes do contrato (camelCase), não pelo tipo C#: um nome trocado aqui quebra o teste.
    private static List<(string Symbol, decimal Exposure, decimal Remaining)> ReadExposureEntries(JsonElement exposuresJson) =>
        exposuresJson.GetProperty("exposures").EnumerateArray()
            .Select(exposureEntry => (exposureEntry.GetProperty("symbol").GetString()!,
                exposureEntry.GetProperty("exposure").GetDecimal(), exposureEntry.GetProperty("remaining").GetDecimal()))
            .ToList();
}
