using Base.OrderAccumulator.Application.Exposures.Interfaces;
using Base.OrderAccumulator.Application.Orders.Interfaces;
using Base.OrderAccumulator.Application.Orders.Responses;
using Base.OrderAccumulator.Application.Orders.UseCases;
using Base.OrderAccumulator.Commons.Database;
using Base.OrderAccumulator.Domain.DomainServices;
using Base.OrderAccumulator.Domain.Orders.Interfaces;
using Base.OrderAccumulator.Domain.Orders.ValueObjects;
using Base.OrderAccumulator.Infrastructure.Exposures.Adapters;
using Base.OrderAccumulator.Infrastructure.Exposures.Repositories;
using Base.OrderAccumulator.Infrastructure.Orders.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace Base.OrderAccumulator.Tests;

// Runs the DecideIncomingOrderUseCase the way the app does: one unit of work (one connection) per
// order, against the test database. The order repository can be wrapped to make it fail on purpose.
public sealed class DecideIncomingOrderTestRunner(
    IDatabaseConnectionSource orderDatabaseConnectionSource,
    ISymbolExposureMemoryPort symbolExposureMemory,
    IOrderMetricsPort orderMetrics,
    Func<IOrderRepository, IOrderRepository>? wrapOrderRepository = null)
{
    public DecideIncomingOrderTestRunner(IDatabaseConnectionSource orderDatabaseConnectionSource)
        : this(orderDatabaseConnectionSource, new InMemorySymbolExposureAdapter(), new UncountedOrderMetrics())
    {
    }

    public async Task<DecideIncomingOrderResponse> DecideIncomingOrderAsync(IncomingOrder incomingOrder, CancellationToken cancellationToken = default)
    {
        await using var orderDatabaseUnitOfWork = new DatabaseUnitOfWork(orderDatabaseConnectionSource);
        var orderDatabase = new DapperDatabase(orderDatabaseUnitOfWork);
        IOrderRepository orderRepository = new OrderRepository(orderDatabase);
        if (wrapOrderRepository is not null)
            orderRepository = wrapOrderRepository(orderRepository);

        var decideIncomingOrderUseCase = new DecideIncomingOrderUseCase(
            orderDatabaseUnitOfWork,
            orderRepository,
            new OrderDecisionDomainService(new ExposureRepository(orderDatabase)),
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
    public static async Task<DecideIncomingOrderResponse> DecideIncomingOrderAsync(this IServiceProvider orderAccumulatorAppServices, IncomingOrder incomingOrder)
    {
        await using var orderOperationScope = orderAccumulatorAppServices.CreateAsyncScope();
        return await orderOperationScope.ServiceProvider.GetRequiredService<DecideIncomingOrderUseCase>().DecideIncomingOrderAsync(incomingOrder);
    }
}
