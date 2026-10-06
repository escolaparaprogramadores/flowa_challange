using System.Net;
using System.Text.Json;

namespace Flowa.OrderAccumulator.Tests;

// CA-17 (part of F3): GET /api/exposures in the contract format, before and after orders through FIX.
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

        await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder("buy-petr4", "PETR4", '1', 100, 10.50m));
        await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder("sell-vale3", "VALE3", '2', 20, 25.00m));
        var exposuresAfterAccepted = ReadExposureEntries(await GetExposuresJsonAsync(orderAccumulatorTestApp));

        var rejectedOrderExecutionReport = await fixTestInitiator.SendExpectingExecutionReportAsync(FixTestInitiator.NewOrder("rejected-viia4", "VIIA4", '1', 100_000, 1.00m));
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
        var exposuresDataMessage = await HttpContractAssertions.ReadSuccessDataMessageAsync(
            await orderAccumulatorTestApp.CreateClient().GetAsync("/api/exposures"));
        Assert.Equal("Exposição dos símbolos lida.", exposuresDataMessage.GetProperty("message").GetString());
        return exposuresDataMessage.GetProperty("data");
    }

    // Reads by the contract names (camelCase), not by the C# type: a name changed here breaks the test.
    private static List<(string Symbol, decimal Exposure, decimal Remaining)> ReadExposureEntries(JsonElement exposuresJson) =>
        exposuresJson.GetProperty("exposures").EnumerateArray()
            .Select(exposureEntry => (exposureEntry.GetProperty("symbol").GetString()!,
                exposureEntry.GetProperty("exposure").GetDecimal(), exposureEntry.GetProperty("remaining").GetDecimal()))
            .ToList();
}
