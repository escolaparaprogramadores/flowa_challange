using System.Net;
using System.Text.Json;
using System.Text;
using Base.OrderAccumulator.Application.Exposures.Interfaces;
using Base.OrderAccumulator.Application.Orders.UseCases;
using Base.OrderAccumulator.Commons.Database;
using Base.OrderAccumulator.Commons.Responses;
using Base.OrderAccumulator.Domain.Exposures.ValueObjects;
using Base.OrderAccumulator.Infrastructure.DependencyInjection;
using Base.OrderAccumulator.Infrastructure.Exposures.Adapters;
using Base.OrderAccumulator.Infrastructure.Exposures.Repositories;
using Base.OrderAccumulator.Infrastructure.Orders.Repositories;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Base.OrderAccumulator.Tests;

// CA-20, CA-22, CA-29 and CA-30: DELETE /api/orders against the real PostgreSQL.
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
        var appSymbolExposureMemory = orderAccumulatorTestApp.Services.GetRequiredService<ISymbolExposureMemoryPort>();
        await ProcessOneOrderOnEachSymbolAsync(orderAccumulatorTestApp);

        await orderAccumulatorTestApp.CreateClient().DeleteAsync("/api/orders");
        var exposureMemoryAfterDelete = appSymbolExposureMemory.ReadCurrentSymbolExposures();
        var orderDecisionAfterDelete = await orderAccumulatorTestApp.Services.DecideIncomingOrderAsync(TestOrders.NewBuyOrder("VALE3", 10, 2.50m));

        Assert.Equal(ZeroedSymbolExposures, exposureMemoryAfterDelete);
        Assert.True(orderDecisionAfterDelete.Accepted);
        SymbolExposure[] exposuresAfterTheNewOrder = [new("PETR4", 0m), new("VALE3", 25.00m), new("VIIA4", 0m)];
        Assert.Equal(exposuresAfterTheNewOrder, appSymbolExposureMemory.ReadCurrentSymbolExposures());
        Assert.Equal(exposuresAfterTheNewOrder, await orderAccumulatorDatabase.ExposureReader.GetSymbolExposuresAsync());
    }

    [Fact]
    public async Task Get_exposures_shows_zero_on_the_three_symbols_after_delete()
    {
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();
        await ProcessOneOrderOnEachSymbolAsync(orderAccumulatorTestApp);
        var orderAccumulatorClient = orderAccumulatorTestApp.CreateClient();

        await orderAccumulatorClient.DeleteAsync("/api/orders");
        var exposuresJson = JsonDocument.Parse(await orderAccumulatorClient.GetStringAsync("/api/exposures")).RootElement.GetProperty("data");

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
    public async Task Post_on_the_orders_route_returns_405_and_deletes_nothing(string? ordersPostBody)
    {
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();
        await ProcessOneOrderOnEachSymbolAsync(orderAccumulatorTestApp);
        var exposuresBeforeThePost = await orderAccumulatorDatabase.ExposureReader.GetSymbolExposuresAsync();

        var postOrdersResponse = await orderAccumulatorTestApp.CreateClient().PostAsync(
            "/api/orders", ordersPostBody is null ? null : new StringContent(ordersPostBody, Encoding.UTF8, "application/json"));

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

        var preflightResponse = await orderAccumulatorClient.SendAsync(NewOrdersRequestFromOtherSite(HttpMethod.Options, ("Access-Control-Request-Method", "DELETE")));
        var ordersCountAfterPreflight = await orderAccumulatorDatabase.CountStoredOrdersAsync();
        var getFromOtherSiteResponse = await orderAccumulatorClient.SendAsync(NewOrdersRequestFromOtherSite(HttpMethod.Get));
        var deleteFromOtherSiteResponse = await orderAccumulatorClient.SendAsync(NewOrdersRequestFromOtherSite(HttpMethod.Delete));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, preflightResponse.StatusCode);
        Assert.Equal(3L, ordersCountAfterPreflight);
        Assert.Equal(HttpStatusCode.OK, getFromOtherSiteResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, deleteFromOtherSiteResponse.StatusCode);
        foreach (var ordersRouteResponse in new[] { preflightResponse, getFromOtherSiteResponse, deleteFromOtherSiteResponse })
            Assert.False(ordersRouteResponse.Headers.Contains("Access-Control-Allow-Origin"), $"{ordersRouteResponse.RequestMessage!.Method} trouxe Access-Control-Allow-Origin");
    }

    // All or nothing: a guard that exists only in this test database makes the DELETE on orders fail. It first checks
    // that the exposure was already zeroed in the same transaction (P0001); if the orders were deleted first, the error
    // would be P0002. Nothing may stay zeroed, neither in the database nor in memory.
    [Fact]
    public async Task Failed_delete_rolls_back_the_zeroed_exposures_and_leaves_the_memory_untouched()
    {
        var deleteFailureDatabaseConnectionString = await CreateDatabaseWhereDeletingOrdersFailsAsync();
        await using var deleteFailureDataSource = NpgsqlDataSource.Create(deleteFailureDatabaseConnectionString);
        await using var deleteFailureConnectionSource = new PostgresConnectionSource(deleteFailureDatabaseConnectionString);
        var deleteFailureExposureReader = new SymbolExposureReaderWithOwnConnection(deleteFailureConnectionSource);
        await new DecideIncomingOrderTestRunner(deleteFailureConnectionSource).DecideIncomingOrderAsync(TestOrders.NewBuyOrder("PETR4", 100, 10.00m));
        var symbolExposureMemory = new InMemorySymbolExposureAdapter();
        symbolExposureMemory.LoadStoredExposures(await deleteFailureExposureReader.GetSymbolExposuresAsync());
        await using var deleteFailureUnitOfWork = new DatabaseUnitOfWork(deleteFailureConnectionSource);
        var deleteFailureDatabase = new DapperDatabase(deleteFailureUnitOfWork);
        var deleteAllOrdersUseCase = new DeleteAllOrdersUseCase(
            deleteFailureUnitOfWork, new OrderRepository(deleteFailureDatabase), new ExposureRepository(deleteFailureDatabase), symbolExposureMemory,
            TestObservability.CreateOperationMonitoring(), TestObservability.CreateDiscardingLogger<DeleteAllOrdersUseCase>());

        var refusedDeleteMessage = await deleteAllOrdersUseCase.DeleteAllOrdersAsync(CancellationToken.None);

        Assert.Equal((false, ResultStatus.InternalError, "internal-error"), (refusedDeleteMessage.Success, refusedDeleteMessage.Status, refusedDeleteMessage.ErrorCode));
        var refusedDeleteException = Assert.IsType<PostgresException>(refusedDeleteMessage.UnexpectedFailure);
        Assert.Equal("P0001", refusedDeleteException.SqlState);
        SymbolExposure[] exposuresBeforeTheFailedDelete = [new("PETR4", 1_000.00m), new("VALE3", 0m), new("VIIA4", 0m)];
        Assert.Equal(exposuresBeforeTheFailedDelete, await deleteFailureExposureReader.GetSymbolExposuresAsync());
        Assert.Equal(exposuresBeforeTheFailedDelete, symbolExposureMemory.ReadCurrentSymbolExposures());
        await using var deleteFailureConnection = await deleteFailureDataSource.OpenConnectionAsync();
        Assert.Equal(1L, await deleteFailureConnection.ExecuteScalarAsync<long>("SELECT count(*) FROM orders"));
    }

    private static HttpRequestMessage NewOrdersRequestFromOtherSite(HttpMethod ordersRouteMethod, params (string Name, string Value)[] extraRequestHeaders)
    {
        var ordersRequestFromOtherSite = new HttpRequestMessage(ordersRouteMethod, "/api/orders");
        ordersRequestFromOtherSite.Headers.Add("Origin", OtherSiteOrigin);
        foreach (var (requestHeaderName, requestHeaderValue) in extraRequestHeaders)
            ordersRequestFromOtherSite.Headers.Add(requestHeaderName, requestHeaderValue);
        return ordersRequestFromOtherSite;
    }

    private static async Task ProcessOneOrderOnEachSymbolAsync(OrderAccumulatorFixTestHost orderAccumulatorTestApp)
    {
        var appOrderDecisionServices = orderAccumulatorTestApp.Services;
        Assert.True((await appOrderDecisionServices.DecideIncomingOrderAsync(TestOrders.NewBuyOrder("PETR4", 100, 10.50m))).Accepted);
        Assert.True((await appOrderDecisionServices.DecideIncomingOrderAsync(TestOrders.NewSellOrder("VALE3", 20, 25.00m))).Accepted);
        Assert.True((await appOrderDecisionServices.DecideIncomingOrderAsync(TestOrders.NewBuyOrder("VIIA4", 3, 7.00m))).Accepted);
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

        await using (var deleteFailureSchemaSource = new PostgresConnectionSource(deleteFailureDatabaseConnectionString))
            await deleteFailureSchemaSource.ApplyOrderAccumulatorSchemaAsync();
        await using var deleteFailureDataSource = NpgsqlDataSource.Create(deleteFailureDatabaseConnectionString);
        await using var deleteFailureConnection = await deleteFailureDataSource.OpenConnectionAsync();
        await deleteFailureConnection.ExecuteAsync(
            """
            CREATE FUNCTION refuse_deleting_orders() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF (SELECT exposure FROM exposures WHERE symbol = 'PETR4') <> 0 THEN
                    RAISE EXCEPTION USING ERRCODE = 'P0002', MESSAGE = 'the orders were deleted before the exposure was zeroed';
                END IF;
                RAISE EXCEPTION 'deleting orders refused in this test database';
            END $$;
            CREATE TRIGGER refuse_deleting_orders BEFORE DELETE ON orders FOR EACH STATEMENT EXECUTE FUNCTION refuse_deleting_orders();
            """);
        return deleteFailureDatabaseConnectionString;
    }
}
