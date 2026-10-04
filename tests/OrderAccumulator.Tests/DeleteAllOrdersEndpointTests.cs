using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OrderAccumulator.Exposure;
using OrderAccumulator.Observabilidade;
using OrderAccumulator.Persistence;

namespace OrderAccumulator.Tests;

// CA-20, CA-22, CA-29 e CA-30: DELETE /api/orders contra o PostgreSQL real.
[Collection(OrderAccumulatorPostgresCollection.Name)]
public sealed class DeleteAllOrdersEndpointTests(OrderAccumulatorPostgresFixture orderAccumulatorDatabase) : IAsyncLifetime
{
    private const string OtherSiteOrigin = "https://outro-site.example";

    private static readonly SymbolExposure[] ZeroedSymbolExposures =
        [new("PETR4", 0m), new("VALE3", 0m), new("VIIA4", 0m)];

    public Task InitializeAsync() => orderAccumulatorDatabase.ResetOrdersAndExposuresAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Delete_removes_every_order_zeroes_the_three_exposures_and_keeps_their_rows()
    {
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();
        await ProcessOneOrderOnEachSymbolAsync(orderAccumulatorTestApp);

        var deleteAllOrdersResponse = await orderAccumulatorTestApp.CreateClient().DeleteAsync("/api/orders");

        Assert.Equal(HttpStatusCode.NoContent, deleteAllOrdersResponse.StatusCode);
        Assert.Equal("", await deleteAllOrdersResponse.Content.ReadAsStringAsync());
        Assert.Equal(0L, await orderAccumulatorDatabase.CountStoredOrdersAsync());
        Assert.Equal(ZeroedSymbolExposures, await orderAccumulatorDatabase.ExposureReader.GetSymbolExposuresAsync());
        Assert.Equal(3L, await CountExposureRowsAsync());
    }

    [Fact]
    public async Task Delete_zeroes_the_exposure_memory_and_a_new_order_adds_up_from_zero_in_the_database_and_in_memory()
    {
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();
        var appSymbolExposureMemory = orderAccumulatorTestApp.Services.GetRequiredService<SymbolExposureMemory>();
        await ProcessOneOrderOnEachSymbolAsync(orderAccumulatorTestApp);

        await orderAccumulatorTestApp.CreateClient().DeleteAsync("/api/orders");
        var exposureMemoryAfterDelete = appSymbolExposureMemory.CurrentSymbolExposures();
        var orderAfterDelete = await orderAccumulatorTestApp.Services.GetRequiredService<IOrderProcessor>()
            .ProcessIncomingOrderAsync(TestOrders.NewBuyOrder("VALE3", 10, 2.50m));

        Assert.Equal(ZeroedSymbolExposures, exposureMemoryAfterDelete);
        Assert.True(orderAfterDelete.Accepted);
        SymbolExposure[] exposuresAfterTheNewOrder = [new("PETR4", 0m), new("VALE3", 25.00m), new("VIIA4", 0m)];
        Assert.Equal(exposuresAfterTheNewOrder, appSymbolExposureMemory.CurrentSymbolExposures());
        Assert.Equal(exposuresAfterTheNewOrder, await orderAccumulatorDatabase.ExposureReader.GetSymbolExposuresAsync());
    }

    [Fact]
    public async Task Get_exposures_shows_zero_on_the_three_symbols_after_delete()
    {
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();
        await ProcessOneOrderOnEachSymbolAsync(orderAccumulatorTestApp);
        var orderAccumulatorClient = orderAccumulatorTestApp.CreateClient();

        await orderAccumulatorClient.DeleteAsync("/api/orders");
        var exposuresJson = JsonDocument.Parse(await orderAccumulatorClient.GetStringAsync("/api/exposures")).RootElement;

        Assert.Equal(
            [("PETR4", 0m, 100_000_000m), ("VALE3", 0m, 100_000_000m), ("VIIA4", 0m, 100_000_000m)],
            exposuresJson.GetProperty("exposures").EnumerateArray()
                .Select(exposureEntry => (exposureEntry.GetProperty("symbol").GetString()!,
                    exposureEntry.GetProperty("exposure").GetDecimal(), exposureEntry.GetProperty("remaining").GetDecimal()))
                .ToList());
    }

    [Fact]
    public async Task Get_on_the_orders_route_only_lists_and_deletes_nothing()
    {
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();
        await ProcessOneOrderOnEachSymbolAsync(orderAccumulatorTestApp);
        var exposuresBeforeTheGet = await orderAccumulatorDatabase.ExposureReader.GetSymbolExposuresAsync();

        var getOrdersResponse = await orderAccumulatorTestApp.CreateClient().GetAsync("/api/orders");

        Assert.Equal(HttpStatusCode.OK, getOrdersResponse.StatusCode);
        Assert.Equal(3L, await orderAccumulatorDatabase.CountStoredOrdersAsync());
        Assert.Equal(exposuresBeforeTheGet, await orderAccumulatorDatabase.ExposureReader.GetSymbolExposuresAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    public async Task Post_on_the_orders_route_returns_405_and_deletes_nothing(string? postBody)
    {
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();
        await ProcessOneOrderOnEachSymbolAsync(orderAccumulatorTestApp);
        var exposuresBeforeThePost = await orderAccumulatorDatabase.ExposureReader.GetSymbolExposuresAsync();

        var postOrdersResponse = await orderAccumulatorTestApp.CreateClient().PostAsync(
            "/api/orders", postBody is null ? null : new StringContent(postBody, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, postOrdersResponse.StatusCode);
        Assert.Equal(3L, await orderAccumulatorDatabase.CountStoredOrdersAsync());
        Assert.Equal(exposuresBeforeThePost, await orderAccumulatorDatabase.ExposureReader.GetSymbolExposuresAsync());
    }

    [Fact]
    public async Task Orders_route_never_sends_access_control_allow_origin_to_another_site()
    {
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();
        await ProcessOneOrderOnEachSymbolAsync(orderAccumulatorTestApp);
        var orderAccumulatorClient = orderAccumulatorTestApp.CreateClient();

        var preflightResponse = await orderAccumulatorClient.SendAsync(OrdersRequestFromOtherSite(HttpMethod.Options, ("Access-Control-Request-Method", "DELETE")));
        var ordersCountAfterPreflight = await orderAccumulatorDatabase.CountStoredOrdersAsync();
        var getFromOtherSiteResponse = await orderAccumulatorClient.SendAsync(OrdersRequestFromOtherSite(HttpMethod.Get));
        var deleteFromOtherSiteResponse = await orderAccumulatorClient.SendAsync(OrdersRequestFromOtherSite(HttpMethod.Delete));

        Assert.NotEqual(HttpStatusCode.NoContent, preflightResponse.StatusCode);
        Assert.False(preflightResponse.IsSuccessStatusCode);
        Assert.Equal(3L, ordersCountAfterPreflight);
        Assert.Equal(HttpStatusCode.OK, getFromOtherSiteResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, deleteFromOtherSiteResponse.StatusCode);
        foreach (var ordersRouteResponse in new[] { preflightResponse, getFromOtherSiteResponse, deleteFromOtherSiteResponse })
            Assert.False(ordersRouteResponse.Headers.Contains("Access-Control-Allow-Origin"), $"{ordersRouteResponse.RequestMessage!.Method} trouxe Access-Control-Allow-Origin");
    }

    // Tudo ou nada: uma trava só deste banco de teste faz o DELETE de orders falhar depois que a exposição já
    // foi zerada na mesma transação. Nada pode ficar zerado, nem no banco nem na memória.
    [Fact]
    public async Task Failed_delete_rolls_back_the_zeroed_exposures_and_leaves_the_memory_untouched()
    {
        var deleteFailureDatabaseConnectionString = await CreateDatabaseWhereDeletingOrdersFailsAsync();
        await using var deleteFailureDataSource = NpgsqlDataSource.Create(deleteFailureDatabaseConnectionString);
        var deleteFailureExposureReader = new PostgresExposureReader(deleteFailureDataSource);
        await new PostgresOrderProcessor(deleteFailureDataSource).ProcessIncomingOrderAsync(TestOrders.NewBuyOrder("PETR4", 100, 10.00m));
        var symbolExposureMemory = new SymbolExposureMemory();
        symbolExposureMemory.LoadStoredExposures(await deleteFailureExposureReader.GetSymbolExposuresAsync());
        var orderHistory = new PostgresOrderHistory(deleteFailureDataSource);

        var deleteFailure = await Assert.ThrowsAsync<PostgresException>(() => symbolExposureMemory.DeleteAllOrdersAndZeroExposuresAsync(
            () => orderHistory.DeleteAllOrdersAndZeroExposuresAsync(), CancellationToken.None));

        Assert.Equal("P0001", deleteFailure.SqlState);
        SymbolExposure[] exposuresBeforeTheFailedDelete = [new("PETR4", 1_000.00m), new("VALE3", 0m), new("VIIA4", 0m)];
        Assert.Equal(exposuresBeforeTheFailedDelete, await deleteFailureExposureReader.GetSymbolExposuresAsync());
        Assert.Equal(exposuresBeforeTheFailedDelete, symbolExposureMemory.CurrentSymbolExposures());
        await using var deleteFailureConnection = await deleteFailureDataSource.OpenConnectionAsync();
        Assert.Equal(1L, await deleteFailureConnection.ExecuteScalarAsync<long>("SELECT count(*) FROM orders"));
    }

    private static HttpRequestMessage OrdersRequestFromOtherSite(HttpMethod ordersRouteMethod, params (string Name, string Value)[] extraHeaders)
    {
        var ordersRequestFromOtherSite = new HttpRequestMessage(ordersRouteMethod, "/api/orders");
        ordersRequestFromOtherSite.Headers.Add("Origin", OtherSiteOrigin);
        foreach (var (headerName, headerValue) in extraHeaders)
            ordersRequestFromOtherSite.Headers.Add(headerName, headerValue);
        return ordersRequestFromOtherSite;
    }

    private static async Task ProcessOneOrderOnEachSymbolAsync(OrderAccumulatorFixTestHost orderAccumulatorTestApp)
    {
        var appOrderProcessor = orderAccumulatorTestApp.Services.GetRequiredService<IOrderProcessor>();
        Assert.True((await appOrderProcessor.ProcessIncomingOrderAsync(TestOrders.NewBuyOrder("PETR4", 100, 10.50m))).Accepted);
        Assert.True((await appOrderProcessor.ProcessIncomingOrderAsync(TestOrders.NewSellOrder("VALE3", 20, 25.00m))).Accepted);
        Assert.True((await appOrderProcessor.ProcessIncomingOrderAsync(TestOrders.NewBuyOrder("VIIA4", 3, 7.00m))).Accepted);
    }

    private async Task<long> CountExposureRowsAsync()
    {
        await using var orderDatabaseConnection = await orderAccumulatorDatabase.OrderDatabaseDataSource.OpenConnectionAsync();
        return await orderDatabaseConnection.ExecuteScalarAsync<long>("SELECT count(*) FROM exposures");
    }

    private async Task<string> CreateDatabaseWhereDeletingOrdersFailsAsync()
    {
        var deleteFailureDatabaseName = "apagar_falha_" + Guid.NewGuid().ToString("N");
        await using (var postgresAdminConnection = await orderAccumulatorDatabase.OrderDatabaseDataSource.OpenConnectionAsync())
            await postgresAdminConnection.ExecuteAsync($"CREATE DATABASE {deleteFailureDatabaseName}");
        var deleteFailureDatabaseConnectionString = new NpgsqlConnectionStringBuilder(orderAccumulatorDatabase.OrderDatabaseConnectionString)
        {
            Database = deleteFailureDatabaseName
        }.ConnectionString;

        await using var deleteFailureDataSource = NpgsqlDataSource.Create(deleteFailureDatabaseConnectionString);
        await deleteFailureDataSource.ApplyOrderAccumulatorSchemaAsync();
        await using var deleteFailureConnection = await deleteFailureDataSource.OpenConnectionAsync();
        await deleteFailureConnection.ExecuteAsync(
            """
            CREATE FUNCTION refuse_deleting_orders() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'apagar ordens recusado neste banco de teste';
            END $$;
            CREATE TRIGGER refuse_deleting_orders BEFORE DELETE ON orders FOR EACH STATEMENT EXECUTE FUNCTION refuse_deleting_orders();
            """);
        return deleteFailureDatabaseConnectionString;
    }
}
