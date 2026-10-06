using Base.OrderAccumulator.Application.Exposures.Interfaces;
using Base.OrderAccumulator.Application.Orders.Interfaces;
using Base.OrderAccumulator.Application.Orders.UseCases;
using Base.OrderAccumulator.Commons.Database;
using Base.OrderAccumulator.Commons.Responses;
using Base.OrderAccumulator.Domain.DomainServices;
using Base.OrderAccumulator.Domain.Exposures.Interfaces;
using Base.OrderAccumulator.Domain.Exposures.ValueObjects;
using Base.OrderAccumulator.Domain.Orders.Enums;
using Base.OrderAccumulator.Domain.Orders.Interfaces;
using Base.OrderAccumulator.Domain.Orders.ValueObjects;
using Base.OrderAccumulator.Infrastructure.DependencyInjection;
using Base.OrderAccumulator.Infrastructure.Exposures.Adapters;
using Base.OrderAccumulator.Infrastructure.Exposures.Repositories;
using Base.OrderAccumulator.Infrastructure.Orders.Repositories;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace Base.OrderAccumulator.Tests;

// CA-18 (part of F2), D-8, D-11 and the support for D-13: what gets stored and what comes back on a repeat.
[Collection(OrderAccumulatorPostgresCollection.Name)]
public sealed class OrderStorageTests(OrderAccumulatorPostgresFixture orderAccumulatorDatabase) : IAsyncLifetime
{
    public Task InitializeAsync() => orderAccumulatorDatabase.ResetOrdersAndExposuresAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Repeated_accepted_order_returns_the_original_answer_and_counts_once()
    {
        var acceptableOrder = TestOrders.NewBuyOrder("VALE3", 100, 10.00m);

        var firstOrderDecision = await orderAccumulatorDatabase.OrderDecisionRunner.DecideIncomingOrderAsync(acceptableOrder);
        var repeatedOrderDecision = await orderAccumulatorDatabase.OrderDecisionRunner.DecideIncomingOrderAsync(acceptableOrder);

        Assert.True(firstOrderDecision.Accepted);
        Assert.False(firstOrderDecision.IsRepeat);
        Assert.Equal(firstOrderDecision with { IsRepeat = true }, repeatedOrderDecision);
        Assert.Equal(1_000.00m, await orderAccumulatorDatabase.ReadExposureOfSymbolAsync("VALE3"));
        Assert.Equal(1, await orderAccumulatorDatabase.CountStoredOrdersAsync(acceptableOrder.ClOrdId));
    }

    [Fact]
    public async Task Repeated_order_rejected_by_the_limit_stays_rejected_even_after_room_opens()
    {
        await orderAccumulatorDatabase.OrderDecisionRunner.DecideIncomingOrderAsync(TestOrders.NewBuyOrder("PETR4", 50_000, 999.99m));
        await orderAccumulatorDatabase.OrderDecisionRunner.DecideIncomingOrderAsync(TestOrders.NewBuyOrder("PETR4", 50_000, 999.99m));
        await orderAccumulatorDatabase.OrderDecisionRunner.DecideIncomingOrderAsync(TestOrders.NewBuyOrder("PETR4", 1_000, 1.00m));
        var orderPastTheLimit = TestOrders.NewBuyOrder("PETR4", 1, 1.00m);

        var firstOrderDecision = await orderAccumulatorDatabase.OrderDecisionRunner.DecideIncomingOrderAsync(orderPastTheLimit);
        await orderAccumulatorDatabase.OrderDecisionRunner.DecideIncomingOrderAsync(TestOrders.NewSellOrder("PETR4", 10, 100.00m));
        var repeatedOrderDecision = await orderAccumulatorDatabase.OrderDecisionRunner.DecideIncomingOrderAsync(orderPastTheLimit);

        Assert.False(firstOrderDecision.Accepted);
        Assert.Equal(ExposureLimitPolicy.BuildExposureLimitRejectionText("PETR4"), firstOrderDecision.RejectReason);
        Assert.Equal(firstOrderDecision with { IsRepeat = true }, repeatedOrderDecision);
        Assert.Equal(ExposureLimitPolicy.PerSymbol - 1_000m, await orderAccumulatorDatabase.ReadExposureOfSymbolAsync("PETR4"));
    }

    [Fact]
    public async Task Invalid_order_is_stored_as_rejected_with_the_contract_text_and_repeats_the_same_answer()
    {
        var invalidOrder = new IncomingOrder(Guid.NewGuid().ToString("N"), "ABCD", OrderSideCodes.BuyOrderSideFixCode, 1.5m, 10.00m);

        var firstOrderDecision = await orderAccumulatorDatabase.OrderDecisionRunner.DecideIncomingOrderAsync(invalidOrder);
        var repeatedOrderDecision = await orderAccumulatorDatabase.OrderDecisionRunner.DecideIncomingOrderAsync(invalidOrder);

        Assert.False(firstOrderDecision.Accepted);
        Assert.Equal($"{OrderFieldMessages.OrderSymbolInvalidMessage} {OrderFieldMessages.OrderQuantityNotIntegerMessage}", firstOrderDecision.RejectReason);
        Assert.Equal(firstOrderDecision with { IsRepeat = true }, repeatedOrderDecision);
        Assert.Equal(1, await orderAccumulatorDatabase.CountStoredOrdersAsync(invalidOrder.ClOrdId));
        Assert.Equal(
            [new SymbolExposure("PETR4", 0m), new("VALE3", 0m), new("VIIA4", 0m)],
            await orderAccumulatorDatabase.ExposureReader.GetSymbolExposuresAsync());
    }

    [Fact]
    public async Task Every_order_is_stored_with_its_answer()
    {
        var acceptedOrderDecision = await orderAccumulatorDatabase.OrderDecisionRunner.DecideIncomingOrderAsync(TestOrders.NewBuyOrder("VIIA4", 100, 2.50m));
        var rejectedOrderDecision = await orderAccumulatorDatabase.OrderDecisionRunner.DecideIncomingOrderAsync(TestOrders.NewSellOrder("VIIA4", 1, 1_000.00m));

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
                new StoredOrderInDatabase(acceptedOrderDecision.ClOrdId, acceptedOrderDecision.OrderId, acceptedOrderDecision.ExecId,
                    "VIIA4", "1", 100m, 2.50m, true, null),
                new StoredOrderInDatabase(rejectedOrderDecision.ClOrdId, rejectedOrderDecision.OrderId, rejectedOrderDecision.ExecId,
                    "VIIA4", "2", 1m, 1_000.00m, false, OrderFieldMessages.OrderPriceTooLargeMessage)
            ],
            storedOrders);
    }

    [Fact]
    public async Task Same_order_sent_many_times_at_once_is_counted_once()
    {
        var repeatedOrder = TestOrders.NewBuyOrder("VALE3", 1_000, 10.00m);
        var repeatStartSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var simultaneousRepeatTasks = Enumerable.Range(0, 50)
            .Select(_ => Task.Run(async () =>
            {
                await repeatStartSignal.Task;
                return await orderAccumulatorDatabase.OrderDecisionRunner.DecideIncomingOrderAsync(repeatedOrder);
            }))
            .ToList();

        repeatStartSignal.SetResult();
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
        await orderAccumulatorDatabase.OrderDecisionRunner.DecideIncomingOrderAsync(TestOrders.NewBuyOrder("PETR4", 10, 10.00m));

        await orderAccumulatorDatabase.OrderDatabaseConnectionSource.ApplyOrderAccumulatorSchemaAsync();

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

        var missingRowDecisionMessage = await orderAccumulatorDatabase.OrderDecisionRunner.DecideIncomingOrderMessageAsync(TestOrders.NewBuyOrder("VIIA4", 1, 1.00m));

        Assert.Equal((false, ResultStatus.InternalError, "internal-error"),
            (missingRowDecisionMessage.Success, missingRowDecisionMessage.Status, missingRowDecisionMessage.ErrorCode));
        var missingRowError = Assert.IsType<InvalidOperationException>(missingRowDecisionMessage.UnexpectedFailure);
        Assert.Equal("The symbol VIIA4 has no exposure row. The database migration was not applied.", missingRowError.Message);
        Assert.Equal(0, await orderAccumulatorDatabase.CountStoredOrdersAsync());
    }

    [Fact]
    public async Task Reading_exposures_without_a_symbol_row_is_an_error()
    {
        await using (var orderDatabaseConnection = await orderAccumulatorDatabase.OrderDatabaseDataSource.OpenConnectionAsync())
            await orderDatabaseConnection.ExecuteAsync("DELETE FROM exposures WHERE symbol = 'VALE3'");

        var missingRowError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => orderAccumulatorDatabase.ExposureReader.GetSymbolExposuresAsync());

        Assert.Equal("The symbol VALE3 has no exposure row. The database migration was not applied.", missingRowError.Message);
    }

    [Fact]
    public async Task Reader_returns_exposure_and_remaining_room_for_each_symbol()
    {
        await orderAccumulatorDatabase.OrderDecisionRunner.DecideIncomingOrderAsync(TestOrders.NewBuyOrder("PETR4", 100, 10.00m));
        await orderAccumulatorDatabase.OrderDecisionRunner.DecideIncomingOrderAsync(TestOrders.NewSellOrder("VIIA4", 50, 4.00m));

        var symbolExposures = await orderAccumulatorDatabase.ExposureReader.GetSymbolExposuresAsync();

        Assert.Equal(
            [
                ("PETR4", 1_000.00m, ExposureLimitPolicy.PerSymbol - 1_000m),
                ("VALE3", 0m, ExposureLimitPolicy.PerSymbol),
                ("VIIA4", -200.00m, ExposureLimitPolicy.PerSymbol - 200m)
            ],
            symbolExposures.Select(symbolExposure => (symbolExposure.Symbol, symbolExposure.Exposure, symbolExposure.RemainingExposureCapacity)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Order_without_cl_ord_id_is_refused_before_touching_the_database(string blankClOrdId)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => orderAccumulatorDatabase.OrderDecisionRunner.DecideIncomingOrderAsync(
            new IncomingOrder(blankClOrdId, "PETR4", OrderSideCodes.BuyOrderSideFixCode, 1, 1.00m)));

        Assert.Equal(0, await orderAccumulatorDatabase.CountStoredOrdersAsync());
        Assert.Equal(0m, await orderAccumulatorDatabase.ReadExposureOfSymbolAsync("PETR4"));
    }

    [Fact]
    public async Task Services_registered_for_the_app_process_orders_against_the_database()
    {
        var orderAccumulatorAppServices = new ServiceCollection()
            .AddOrderAccumulatorPersistence(orderAccumulatorDatabase.OrderDatabaseConnectionString);
        await using var orderAccumulatorServiceProvider = orderAccumulatorAppServices.BuildServiceProvider();

        await using var orderOperationScope = orderAccumulatorServiceProvider.CreateAsyncScope();
        await using var otherOrderOperationScope = orderAccumulatorServiceProvider.CreateAsyncScope();
        var orderOperationServices = orderOperationScope.ServiceProvider;
        var registeredUnitOfWork = orderOperationServices.GetRequiredService<IUnitOfWork>();
        var registeredOrderRepository = orderOperationServices.GetRequiredService<IOrderRepository>();
        var registeredSymbolExposureRepository = orderOperationServices.GetRequiredService<IExposureRepository>();
        var registeredExposureReader = orderAccumulatorServiceProvider.GetRequiredService<ISymbolExposureReadRepository>();
        var registeredServicesOrderDecision = await new DecideIncomingOrderUseCase(
                registeredUnitOfWork, registeredOrderRepository, new OrderDecisionDomainService(registeredSymbolExposureRepository),
                new InMemorySymbolExposureAdapter(), new UncountedOrderMetrics())
            .DecideIncomingOrderAsync(TestOrders.NewBuyOrder("VALE3", 10, 5.00m));

        Assert.IsType<OrderRepository>(registeredOrderRepository);
        Assert.IsType<ExposureRepository>(registeredSymbolExposureRepository);
        Assert.IsType<SymbolExposureReadRepository>(registeredExposureReader);
        Assert.IsType<OrderListReadRepository>(orderAccumulatorServiceProvider.GetRequiredService<IOrderListReadRepository>());
        Assert.Same(orderOperationServices.GetRequiredService<DatabaseUnitOfWork>(), registeredUnitOfWork);
        Assert.NotSame(registeredUnitOfWork, otherOrderOperationScope.ServiceProvider.GetRequiredService<IUnitOfWork>());
        Assert.True(registeredServicesOrderDecision.Data!.Accepted);
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
