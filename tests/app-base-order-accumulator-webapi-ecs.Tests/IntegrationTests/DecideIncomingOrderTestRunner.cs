using Base.OrderAccumulator.Application.Exposures;
using Base.OrderAccumulator.Application.Orders.DecideIncomingOrder;
using Base.OrderAccumulator.Commons;
using Base.OrderAccumulator.Domain.Orders;
using Base.OrderAccumulator.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Base.OrderAccumulator.Tests;

// Runs the DecideIncomingOrderUseCase the way the app does: one unit of work (one connection) per
// order, against the test database. The order repository can be wrapped to make it fail on purpose.
public sealed class DecideIncomingOrderTestRunner(
    NpgsqlDataSource orderDatabaseDataSource,
    SymbolExposureMemoryService symbolExposureMemory,
    IOrderMetricsPort orderMetrics,
    Func<IOrderRepository, IOrderRepository>? wrapOrderRepository = null)
{
    public DecideIncomingOrderTestRunner(NpgsqlDataSource orderDatabaseDataSource)
        : this(orderDatabaseDataSource, new SymbolExposureMemoryService(), new UncountedOrderMetrics())
    {
    }

    public async Task<DecideIncomingOrderOutput> DecideIncomingOrderAsync(IncomingOrder incomingOrder, CancellationToken cancellationToken = default)
    {
        await using var orderDatabaseUnitOfWork = new PostgresUnitOfWork(orderDatabaseDataSource);
        IOrderRepository orderRepository = new OrderRepository(orderDatabaseUnitOfWork);
        if (wrapOrderRepository is not null)
            orderRepository = wrapOrderRepository(orderRepository);

        var decideIncomingOrderUseCase = new DecideIncomingOrderUseCase(
            orderDatabaseUnitOfWork,
            orderRepository,
            new OrderDecisionDomainService(new ExposureRepository(orderDatabaseUnitOfWork)),
            symbolExposureMemory,
            orderMetrics);
        return await decideIncomingOrderUseCase.DecideIncomingOrderAsync(incomingOrder, cancellationToken);
    }
}

public sealed class UncountedOrderMetrics : IOrderMetricsPort
{
    public void CountAnsweredOrder(string? orderSymbol, char orderSide, bool orderAccepted)
    {
    }

    public void SendSymbolExposureGauge(string orderSymbol, decimal symbolExposure)
    {
    }
}

public static class OrderAccumulatorAppServicesExtensions
{
    // Same scope per order that the FIX consumer opens in the app.
    public static async Task<DecideIncomingOrderOutput> DecideIncomingOrderAsync(this IServiceProvider orderAccumulatorAppServices, IncomingOrder incomingOrder)
    {
        await using var orderOperationScope = orderAccumulatorAppServices.CreateAsyncScope();
        return await orderOperationScope.ServiceProvider.GetRequiredService<DecideIncomingOrderUseCase>().DecideIncomingOrderAsync(incomingOrder);
    }
}
