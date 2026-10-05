using System.Net;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace Base.OrderAccumulator.Tests;

// CA-21, CA-32, CA-33, CA-42 e CA-34: GET /api/orders?page=n contra o PostgreSQL real, no formato do contrato.
[Collection(OrderAccumulatorPostgresCollection.Name)]
public sealed class OrderListEndpointTests(OrderAccumulatorPostgresFixture orderAccumulatorDatabase) : IAsyncLifetime
{
    public Task InitializeAsync() => orderAccumulatorDatabase.ResetOrdersAndExposuresAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Page_one_and_two_list_the_newest_orders_first_ten_per_page_with_the_real_total()
    {
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();
        var clOrdIdsInArrivalOrder = await DecideOrdersOneAfterAnotherAsync(orderAccumulatorTestApp, 12);

        var firstOrderPage = await GetOrderPageJsonAsync(orderAccumulatorTestApp, "/api/orders?page=1");
        var secondOrderPage = await GetOrderPageJsonAsync(orderAccumulatorTestApp, "/api/orders?page=2");

        var clOrdIdsNewestFirst = Enumerable.Reverse(clOrdIdsInArrivalOrder).ToList();
        Assert.Equal((1, 10, 12L), ReadOrderPageHeader(firstOrderPage));
        Assert.Equal(clOrdIdsNewestFirst.Take(10).ToList(), ReadListedClOrdIds(firstOrderPage));
        Assert.Equal((2, 10, 12L), ReadOrderPageHeader(secondOrderPage));
        Assert.Equal(clOrdIdsNewestFirst.Skip(10).ToList(), ReadListedClOrdIds(secondOrderPage));
    }

    [Fact]
    public async Task Each_listed_order_carries_the_eight_contract_fields_for_accepted_and_rejected_orders()
    {
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();
        var appOrderDecisionServices = orderAccumulatorTestApp.Services;
        var acceptedBuyOutcome = await appOrderDecisionServices.DecideIncomingOrderAsync(TestOrders.NewBuyOrder("PETR4", 100, 10.50m));
        var rejectedSellOutcome = await appOrderDecisionServices.DecideIncomingOrderAsync(TestOrders.NewSellOrder("VALE3", 100_000, 1.00m));
        var storedReceivedAtByClOrdId = await ReadStoredReceivedAtByClOrdIdAsync();

        var orderPage = await GetOrderPageJsonAsync(orderAccumulatorTestApp, "/api/orders?page=1");
        var listedOrders = orderPage.GetProperty("orders").EnumerateArray().ToList();

        Assert.False(rejectedSellOutcome.Accepted);
        Assert.Equal((1, 10, 2L), ReadOrderPageHeader(orderPage));
        Assert.Equal(2, listedOrders.Count);
        AssertListedOrder(listedOrders[0], storedReceivedAtByClOrdId[rejectedSellOutcome.ClOrdId], "rejected", "VALE3", "sell", 100_000m, 1.00m,
            rejectedSellOutcome.OrderId, rejectedSellOutcome.ClOrdId);
        AssertListedOrder(listedOrders[1], storedReceivedAtByClOrdId[acceptedBuyOutcome.ClOrdId], "accepted", "PETR4", "buy", 100m, 10.50m,
            acceptedBuyOutcome.OrderId, acceptedBuyOutcome.ClOrdId);
    }

    [Fact]
    public async Task Rejected_order_with_unknown_symbol_and_side_from_fix_lists_the_symbol_and_a_null_side()
    {
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();
        var appOrderDecisionServices = orderAccumulatorTestApp.Services;
        var rejectedUnknownSideOutcome = await appOrderDecisionServices.DecideIncomingOrderAsync(TestOrders.NewIncomingOrder("ABCD3", '7', 10, 1.00m));

        var listedOrder = Assert.Single((await GetOrderPageJsonAsync(orderAccumulatorTestApp, "/api/orders?page=1")).GetProperty("orders").EnumerateArray());

        Assert.False(rejectedUnknownSideOutcome.Accepted);
        Assert.Equal("rejected", listedOrder.GetProperty("status").GetString());
        Assert.Equal("ABCD3", listedOrder.GetProperty("symbol").GetString());
        Assert.Equal(JsonValueKind.Null, listedOrder.GetProperty("side").ValueKind);
    }

    // Duas ordens no mesmo instante: o id maior vem primeiro. Uma ordem mais antiga com id maior vem depois:
    // a data manda, o id só desempata.
    [Fact]
    public async Task Orders_are_sorted_by_received_at_descending_and_ties_by_id_descending()
    {
        await InsertStoredOrderAsync("mesmo-instante-id-menor", "2026-10-04T12:00:02Z");
        await InsertStoredOrderAsync("mais-antiga-id-do-meio", "2026-10-04T12:00:01Z");
        await InsertStoredOrderAsync("mesmo-instante-id-maior", "2026-10-04T12:00:02Z");
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();

        var orderPage = await GetOrderPageJsonAsync(orderAccumulatorTestApp, "/api/orders?page=1");

        Assert.Equal(["mesmo-instante-id-maior", "mesmo-instante-id-menor", "mais-antiga-id-do-meio"], ReadListedClOrdIds(orderPage));
        Assert.Equal(
            ["2026-10-04T12:00:02Z", "2026-10-04T12:00:02Z", "2026-10-04T12:00:01Z"],
            orderPage.GetProperty("orders").EnumerateArray().Select(listedOrder => listedOrder.GetProperty("receivedAt").GetString()).ToList());
    }

    [Fact]
    public async Task Schema_creates_the_received_at_and_id_descending_index_on_orders()
    {
        await using var orderDatabaseConnection = await orderAccumulatorDatabase.OrderDatabaseDataSource.OpenConnectionAsync();

        var orderListIndexDefinition = await orderDatabaseConnection.ExecuteScalarAsync<string>(
            "SELECT indexdef FROM pg_indexes WHERE tablename = 'orders' AND indexname = 'orders_received_at_id_idx'");

        Assert.Equal("CREATE INDEX orders_received_at_id_idx ON public.orders USING btree (received_at DESC, id DESC)", orderListIndexDefinition);
    }

    [Fact]
    public async Task Page_size_sent_by_the_client_is_ignored_and_the_server_keeps_ten()
    {
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();
        await DecideOrdersOneAfterAnotherAsync(orderAccumulatorTestApp, 12);

        var orderPage = await GetOrderPageJsonAsync(orderAccumulatorTestApp, "/api/orders?page=1&pageSize=50");

        Assert.Equal((1, 10, 12L), ReadOrderPageHeader(orderPage));
        Assert.Equal(10, orderPage.GetProperty("orders").GetArrayLength());
    }

    [Theory]
    [InlineData(3)]
    [InlineData(1000)]
    public async Task Page_past_the_last_one_returns_an_empty_list_with_the_real_total(int pastLastPageNumber)
    {
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();
        await DecideOrdersOneAfterAnotherAsync(orderAccumulatorTestApp, 12);

        var orderPage = await GetOrderPageJsonAsync(orderAccumulatorTestApp, $"/api/orders?page={pastLastPageNumber}");

        Assert.Equal((pastLastPageNumber, 10, 12L), ReadOrderPageHeader(orderPage));
        Assert.Equal(0, orderPage.GetProperty("orders").GetArrayLength());
    }

    [Fact]
    public async Task Missing_page_returns_the_first_page()
    {
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();
        var clOrdIdsInArrivalOrder = await DecideOrdersOneAfterAnotherAsync(orderAccumulatorTestApp, 11);

        var orderPage = await GetOrderPageJsonAsync(orderAccumulatorTestApp, "/api/orders");

        Assert.Equal((1, 10, 11L), ReadOrderPageHeader(orderPage));
        Assert.Equal(Enumerable.Reverse(clOrdIdsInArrivalOrder).Take(10).ToList(), ReadListedClOrdIds(orderPage));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("1.5")]
    [InlineData("+1")]
    [InlineData("1001")]
    [InlineData("99999999999")]
    public async Task Invalid_page_returns_400_validation_error_on_the_page_field(string invalidPageQueryValue)
    {
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();

        var invalidPageResponse = await orderAccumulatorTestApp.CreateClient().GetAsync($"/api/orders?page={Uri.EscapeDataString(invalidPageQueryValue)}");

        Assert.Equal(HttpStatusCode.BadRequest, invalidPageResponse.StatusCode);
        Assert.Equal("application/json", invalidPageResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            """{"status":"validation_error","message":"Página inválida.","errors":[{"field":"page","message":"A página deve ser um número inteiro de 1 a 1000."}]}""",
            await invalidPageResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Repeated_page_parameter_returns_400_validation_error()
    {
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();

        var repeatedPageResponse = await orderAccumulatorTestApp.CreateClient().GetAsync("/api/orders?page=1&page=2");

        Assert.Equal(HttpStatusCode.BadRequest, repeatedPageResponse.StatusCode);
        Assert.Equal(
            """{"status":"validation_error","message":"Página inválida.","errors":[{"field":"page","message":"A página deve ser um número inteiro de 1 a 1000."}]}""",
            await repeatedPageResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Listing_orders_writes_no_information_log_per_call()
    {
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();
        await DecideOrdersOneAfterAnotherAsync(orderAccumulatorTestApp, 2);
        var logLineCountBeforeTheListing = orderAccumulatorTestApp.CapturedOrderAccumulatorLogs.CapturedLogLines.Count;

        await GetOrderPageJsonAsync(orderAccumulatorTestApp, "/api/orders?page=1");
        await GetOrderPageJsonAsync(orderAccumulatorTestApp, "/api/orders?page=2");
        await orderAccumulatorTestApp.CreateClient().GetAsync("/api/orders?page=abc");

        var informationLogLinesOfTheListing = orderAccumulatorTestApp.CapturedOrderAccumulatorLogs.CapturedLogLines
            .Skip(logLineCountBeforeTheListing)
            .Where(capturedLogLine => !capturedLogLine.StartsWith("Trace ") && !capturedLogLine.StartsWith("Debug "))
            .Where(capturedLogLine => !capturedLogLine.Contains(" QuickFix") && !capturedLogLine.Contains(" Base.OrderAccumulator.Entrypoint.Fix."))
            .ToList();
        Assert.Empty(informationLogLinesOfTheListing);
    }

    // Uma por vez, para cada ordem ter received_at e id maiores que a anterior.
    private static async Task<List<string>> DecideOrdersOneAfterAnotherAsync(OrderAccumulatorFixTestHost orderAccumulatorTestApp, int orderCount)
    {
        var appOrderDecisionServices = orderAccumulatorTestApp.Services;
        var clOrdIdsInArrivalOrder = new List<string>();
        for (var orderNumber = 0; orderNumber < orderCount; orderNumber++)
        {
            var orderDecision = await appOrderDecisionServices.DecideIncomingOrderAsync(TestOrders.NewBuyOrder("PETR4", 1, 1.00m));
            clOrdIdsInArrivalOrder.Add(orderDecision.ClOrdId);
        }
        return clOrdIdsInArrivalOrder;
    }

    private async Task InsertStoredOrderAsync(string clOrdId, string receivedAtUtc)
    {
        await using var orderDatabaseConnection = await orderAccumulatorDatabase.OrderDatabaseDataSource.OpenConnectionAsync();
        await orderDatabaseConnection.ExecuteAsync(
            """
            INSERT INTO orders (cl_ord_id, order_id, exec_id, symbol, side, quantity, price, accepted, received_at)
            VALUES (@ClOrdId, @ClOrdId, @ClOrdId, 'PETR4', '1', 1, 1.00, true, @ReceivedAt)
            """,
            new { ClOrdId = clOrdId, ReceivedAt = DateTime.Parse(receivedAtUtc, null, System.Globalization.DateTimeStyles.AdjustToUniversal) });
    }

    private async Task<Dictionary<string, DateTime>> ReadStoredReceivedAtByClOrdIdAsync()
    {
        await using var orderDatabaseConnection = await orderAccumulatorDatabase.OrderDatabaseDataSource.OpenConnectionAsync();
        var storedReceivedAts = await orderDatabaseConnection.QueryAsync<(string ClOrdId, DateTime ReceivedAt)>("SELECT cl_ord_id, received_at FROM orders");
        return storedReceivedAts.ToDictionary(storedReceivedAt => storedReceivedAt.ClOrdId, storedReceivedAt => storedReceivedAt.ReceivedAt);
    }

    private static async Task<JsonElement> GetOrderPageJsonAsync(OrderAccumulatorFixTestHost orderAccumulatorTestApp, string orderPageUrl)
    {
        var orderPageResponse = await orderAccumulatorTestApp.CreateClient().GetAsync(orderPageUrl);
        Assert.Equal(HttpStatusCode.OK, orderPageResponse.StatusCode);
        Assert.Equal("application/json", orderPageResponse.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(await orderPageResponse.Content.ReadAsStringAsync()).RootElement;
    }

    private static (int Page, int PageSize, long Total) ReadOrderPageHeader(JsonElement orderPage) =>
        (orderPage.GetProperty("page").GetInt32(), orderPage.GetProperty("pageSize").GetInt32(), orderPage.GetProperty("total").GetInt64());

    private static List<string> ReadListedClOrdIds(JsonElement orderPage) =>
        orderPage.GetProperty("orders").EnumerateArray().Select(listedOrder => listedOrder.GetProperty("clOrdId").GetString()!).ToList();

    // Lê pelos nomes do contrato (camelCase): um nome trocado no código quebra o teste.
    private static void AssertListedOrder(
        JsonElement listedOrder, DateTime storedReceivedAt, string expectedOrderStatus, string expectedOrderSymbol, string expectedOrderSide, decimal expectedOrderQuantity, decimal expectedOrderPrice, string expectedOrderId, string expectedClOrdId)
    {
        Assert.Equal(
            ["receivedAt", "status", "symbol", "side", "quantity", "price", "orderId", "clOrdId"],
            listedOrder.EnumerateObject().Select(listedOrderField => listedOrderField.Name).ToList());
        var listedReceivedAt = listedOrder.GetProperty("receivedAt").GetString()!;
        Assert.EndsWith("Z", listedReceivedAt);
        Assert.Equal(storedReceivedAt.ToUniversalTime(), DateTime.Parse(listedReceivedAt, null, System.Globalization.DateTimeStyles.AdjustToUniversal));
        Assert.Equal(expectedOrderStatus, listedOrder.GetProperty("status").GetString());
        Assert.Equal(expectedOrderSymbol, listedOrder.GetProperty("symbol").GetString());
        Assert.Equal(expectedOrderSide, listedOrder.GetProperty("side").GetString());
        Assert.Equal(expectedOrderQuantity, listedOrder.GetProperty("quantity").GetDecimal());
        Assert.Equal(expectedOrderPrice, listedOrder.GetProperty("price").GetDecimal());
        Assert.Equal(expectedOrderId, listedOrder.GetProperty("orderId").GetString());
        Assert.Equal(expectedClOrdId, listedOrder.GetProperty("clOrdId").GetString());
    }
}
