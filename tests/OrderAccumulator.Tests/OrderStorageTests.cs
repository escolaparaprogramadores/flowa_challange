using Dapper;
using Flowa.Shared;
using Microsoft.Extensions.DependencyInjection;
using OrderAccumulator.Exposure;
using OrderAccumulator.Persistence;

namespace OrderAccumulator.Tests;

// CA-18 (parte da F2), D-8, D-11 e o apoio ao D-13: o que fica gravado e o que volta numa repetição.
[Collection(OrderAccumulatorPostgresCollection.Name)]
public sealed class OrderStorageTests(OrderAccumulatorPostgresFixture orderAccumulatorDatabase) : IAsyncLifetime
{
    public Task InitializeAsync() => orderAccumulatorDatabase.ResetOrdersAndExposuresAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Repeated_accepted_order_returns_the_original_answer_and_counts_once()
    {
        var acceptableOrder = TestOrders.NewBuyOrder("VALE3", 100, 10.00m);

        var firstAnswer = await orderAccumulatorDatabase.OrderProcessor.ProcessIncomingOrderAsync(acceptableOrder);
        var repeatedAnswer = await orderAccumulatorDatabase.OrderProcessor.ProcessIncomingOrderAsync(acceptableOrder);

        Assert.True(firstAnswer.Accepted);
        Assert.False(firstAnswer.IsRepeat);
        Assert.Equal(firstAnswer with { IsRepeat = true }, repeatedAnswer);
        Assert.Equal(1_000.00m, await orderAccumulatorDatabase.ReadExposureOfSymbolAsync("VALE3"));
        Assert.Equal(1, await orderAccumulatorDatabase.CountStoredOrdersAsync(acceptableOrder.ClOrdId));
    }

    [Fact]
    public async Task Repeated_order_rejected_by_the_limit_stays_rejected_even_after_room_opens()
    {
        await orderAccumulatorDatabase.OrderProcessor.ProcessIncomingOrderAsync(TestOrders.NewBuyOrder("PETR4", 50_000, 999.99m));
        await orderAccumulatorDatabase.OrderProcessor.ProcessIncomingOrderAsync(TestOrders.NewBuyOrder("PETR4", 50_000, 999.99m));
        await orderAccumulatorDatabase.OrderProcessor.ProcessIncomingOrderAsync(TestOrders.NewBuyOrder("PETR4", 1_000, 1.00m));
        var orderPastTheLimit = TestOrders.NewBuyOrder("PETR4", 1, 1.00m);

        var firstAnswer = await orderAccumulatorDatabase.OrderProcessor.ProcessIncomingOrderAsync(orderPastTheLimit);
        await orderAccumulatorDatabase.OrderProcessor.ProcessIncomingOrderAsync(TestOrders.NewSellOrder("PETR4", 10, 100.00m));
        var repeatedAnswer = await orderAccumulatorDatabase.OrderProcessor.ProcessIncomingOrderAsync(orderPastTheLimit);

        Assert.False(firstAnswer.Accepted);
        Assert.Equal(ExposureLimit.ExposureLimitRejectionText("PETR4"), firstAnswer.RejectReason);
        Assert.Equal(firstAnswer with { IsRepeat = true }, repeatedAnswer);
        Assert.Equal(ExposureLimit.PerSymbol - 1_000m, await orderAccumulatorDatabase.ReadExposureOfSymbolAsync("PETR4"));
    }

    [Fact]
    public async Task Invalid_order_is_stored_as_rejected_with_the_contract_text_and_repeats_the_same_answer()
    {
        var invalidOrder = new IncomingOrder(Guid.NewGuid().ToString("N"), "ABCD", OrderSideCodes.BuyOrderSideFixCode, 1.5m, 10.00m);

        var firstAnswer = await orderAccumulatorDatabase.OrderProcessor.ProcessIncomingOrderAsync(invalidOrder);
        var repeatedAnswer = await orderAccumulatorDatabase.OrderProcessor.ProcessIncomingOrderAsync(invalidOrder);

        Assert.False(firstAnswer.Accepted);
        Assert.Equal($"{OrderMessages.OrderSymbolInvalidMessage} {OrderMessages.OrderQuantityNotIntegerMessage}", firstAnswer.RejectReason);
        Assert.Equal(firstAnswer with { IsRepeat = true }, repeatedAnswer);
        Assert.Equal(1, await orderAccumulatorDatabase.CountStoredOrdersAsync(invalidOrder.ClOrdId));
        Assert.Equal(
            [new SymbolExposure("PETR4", 0m), new("VALE3", 0m), new("VIIA4", 0m)],
            await orderAccumulatorDatabase.ExposureReader.GetSymbolExposuresAsync());
    }

    [Fact]
    public async Task Every_order_is_stored_with_its_answer()
    {
        var acceptedAnswer = await orderAccumulatorDatabase.OrderProcessor.ProcessIncomingOrderAsync(TestOrders.NewBuyOrder("VIIA4", 100, 2.50m));
        var rejectedAnswer = await orderAccumulatorDatabase.OrderProcessor.ProcessIncomingOrderAsync(TestOrders.NewSellOrder("VIIA4", 1, 1_000.00m));

        await using var orderDatabaseConnection = await orderAccumulatorDatabase.OrderDatabaseDataSource.OpenConnectionAsync();
        var storedOrders = (await orderDatabaseConnection.QueryAsync<StoredOrderInDatabase>(
            """
            SELECT cl_ord_id AS ClOrdId, order_id AS OrderId, exec_id AS ExecId, symbol AS Symbol, side AS Side,
                   quantity AS Quantity, price AS Price, accepted AS Accepted, reject_reason AS RejectReason
            FROM orders
            ORDER BY id
            """)).ToList();

        Assert.Equal(
            [
                new StoredOrderInDatabase(acceptedAnswer.ClOrdId, acceptedAnswer.OrderId, acceptedAnswer.ExecId,
                    "VIIA4", "1", 100m, 2.50m, true, null),
                new StoredOrderInDatabase(rejectedAnswer.ClOrdId, rejectedAnswer.OrderId, rejectedAnswer.ExecId,
                    "VIIA4", "2", 1m, 1_000.00m, false, OrderMessages.OrderPriceTooLargeMessage)
            ],
            storedOrders);
    }

    [Fact]
    public async Task Same_order_sent_many_times_at_once_is_counted_once()
    {
        var repeatedOrder = TestOrders.NewBuyOrder("VALE3", 1_000, 10.00m);
        var startSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var simultaneousRepeatTasks = Enumerable.Range(0, 50)
            .Select(_ => Task.Run(async () =>
            {
                await startSignal.Task;
                return await orderAccumulatorDatabase.OrderProcessor.ProcessIncomingOrderAsync(repeatedOrder);
            }))
            .ToList();

        startSignal.SetResult();
        var repeatAnswers = await Task.WhenAll(simultaneousRepeatTasks);

        Assert.Single(repeatAnswers, repeatAnswer => !repeatAnswer.IsRepeat);
        Assert.Single(repeatAnswers.Select(repeatAnswer => (repeatAnswer.OrderId, repeatAnswer.ExecId, repeatAnswer.Accepted)).Distinct());
        Assert.Equal(10_000.00m, await orderAccumulatorDatabase.ReadExposureOfSymbolAsync("VALE3"));
        Assert.Equal(1, await orderAccumulatorDatabase.CountStoredOrdersAsync(repeatedOrder.ClOrdId));
    }

    [Fact]
    public async Task Money_columns_are_numeric_and_cl_ord_id_is_unique()
    {
        await using var orderDatabaseConnection = await orderAccumulatorDatabase.OrderDatabaseDataSource.OpenConnectionAsync();

        var moneyColumnTypes = (await orderDatabaseConnection.QueryAsync<(string Table, string Column, string Type)>(
            """
            SELECT table_name, column_name, data_type
            FROM information_schema.columns
            WHERE (table_name, column_name) IN (('exposures', 'exposure'), ('orders', 'quantity'), ('orders', 'price'))
            ORDER BY table_name, column_name
            """)).ToList();
        var clOrdIdUniqueConstraint = await orderDatabaseConnection.QuerySingleAsync<string>(
            "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid = 'orders'::regclass AND contype = 'u'");

        Assert.Equal(
            [("exposures", "exposure", "numeric"), ("orders", "price", "numeric"), ("orders", "quantity", "numeric")],
            moneyColumnTypes);
        Assert.Equal("UNIQUE (cl_ord_id)", clOrdIdUniqueConstraint);
    }

    [Fact]
    public async Task Applying_the_schema_again_keeps_what_was_stored()
    {
        await orderAccumulatorDatabase.OrderProcessor.ProcessIncomingOrderAsync(TestOrders.NewBuyOrder("PETR4", 10, 10.00m));

        await orderAccumulatorDatabase.OrderDatabaseDataSource.ApplyOrderAccumulatorSchemaAsync();

        Assert.Equal(
            [new SymbolExposure("PETR4", 100.00m), new("VALE3", 0m), new("VIIA4", 0m)],
            await orderAccumulatorDatabase.ExposureReader.GetSymbolExposuresAsync());
        Assert.Equal(1, await orderAccumulatorDatabase.CountStoredOrdersAsync());
    }

    [Fact]
    public async Task Valid_symbol_without_an_exposure_row_is_an_error_and_not_a_limit_rejection()
    {
        await using (var orderDatabaseConnection = await orderAccumulatorDatabase.OrderDatabaseDataSource.OpenConnectionAsync())
            await orderDatabaseConnection.ExecuteAsync("DELETE FROM exposures WHERE symbol = 'VIIA4'");

        var missingRowError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => orderAccumulatorDatabase.OrderProcessor.ProcessIncomingOrderAsync(TestOrders.NewBuyOrder("VIIA4", 1, 1.00m)));

        Assert.Equal("O símbolo VIIA4 não tem linha de exposição. A migração do banco não foi aplicada.", missingRowError.Message);
        Assert.Equal(0, await orderAccumulatorDatabase.CountStoredOrdersAsync());
    }

    [Fact]
    public async Task Reading_exposures_without_a_symbol_row_is_an_error()
    {
        await using (var orderDatabaseConnection = await orderAccumulatorDatabase.OrderDatabaseDataSource.OpenConnectionAsync())
            await orderDatabaseConnection.ExecuteAsync("DELETE FROM exposures WHERE symbol = 'VALE3'");

        var missingRowError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => orderAccumulatorDatabase.ExposureReader.GetSymbolExposuresAsync());

        Assert.Equal("O símbolo VALE3 não tem linha de exposição. A migração do banco não foi aplicada.", missingRowError.Message);
    }

    [Fact]
    public async Task Reader_returns_exposure_and_remaining_room_for_each_symbol()
    {
        await orderAccumulatorDatabase.OrderProcessor.ProcessIncomingOrderAsync(TestOrders.NewBuyOrder("PETR4", 100, 10.00m));
        await orderAccumulatorDatabase.OrderProcessor.ProcessIncomingOrderAsync(TestOrders.NewSellOrder("VIIA4", 50, 4.00m));

        var symbolExposures = await orderAccumulatorDatabase.ExposureReader.GetSymbolExposuresAsync();

        Assert.Equal(
            [
                ("PETR4", 1_000.00m, ExposureLimit.PerSymbol - 1_000m),
                ("VALE3", 0m, ExposureLimit.PerSymbol),
                ("VIIA4", -200.00m, ExposureLimit.PerSymbol - 200m)
            ],
            symbolExposures.Select(symbolExposure => (symbolExposure.Symbol, symbolExposure.Exposure, symbolExposure.RemainingExposureCapacity)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Order_without_cl_ord_id_is_refused_before_touching_the_database(string blankClOrdId)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => orderAccumulatorDatabase.OrderProcessor.ProcessIncomingOrderAsync(
            new IncomingOrder(blankClOrdId, "PETR4", OrderSideCodes.BuyOrderSideFixCode, 1, 1.00m)));

        Assert.Equal(0, await orderAccumulatorDatabase.CountStoredOrdersAsync());
        Assert.Equal(0m, await orderAccumulatorDatabase.ReadExposureOfSymbolAsync("PETR4"));
    }

    [Fact]
    public async Task Services_registered_for_the_app_process_orders_against_the_database()
    {
        var appServices = new ServiceCollection()
            .AddOrderAccumulatorPersistence(orderAccumulatorDatabase.OrderDatabaseConnectionString);
        await using var appServiceProvider = appServices.BuildServiceProvider();

        var registeredOrderProcessor = appServiceProvider.GetRequiredService<IOrderProcessor>();
        var registeredExposureReader = appServiceProvider.GetRequiredService<IExposureReader>();
        var orderAnswer = await registeredOrderProcessor.ProcessIncomingOrderAsync(TestOrders.NewBuyOrder("VALE3", 10, 5.00m));

        Assert.IsType<PostgresOrderProcessor>(registeredOrderProcessor);
        Assert.IsType<PostgresExposureReader>(registeredExposureReader);
        Assert.Same(registeredOrderProcessor, appServiceProvider.GetRequiredService<IOrderProcessor>());
        Assert.True(orderAnswer.Accepted);
        Assert.Equal(50.00m, (await registeredExposureReader.GetSymbolExposuresAsync())
            .Single(symbolExposure => symbolExposure.Symbol == "VALE3").Exposure);
    }

    private sealed record StoredOrderInDatabase(
        string ClOrdId,
        string OrderId,
        string ExecId,
        string? Symbol,
        string Side,
        decimal Quantity,
        decimal Price,
        bool Accepted,
        string? RejectReason);
}
