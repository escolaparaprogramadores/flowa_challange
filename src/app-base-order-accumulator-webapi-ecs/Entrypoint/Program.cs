using System.Globalization;
using System.Reflection;
using Base.OrderAccumulator.Application.Exposures.GetExposures;
using Base.OrderAccumulator.Application.Exposures;
using Base.OrderAccumulator.Application.Orders.DecideIncomingOrder;
using Base.OrderAccumulator.Application.Orders.DeleteAllOrders;
using Base.OrderAccumulator.Application.Orders.ListOrders;
using Base.OrderAccumulator.Commons;
using Base.OrderAccumulator.Domain.Exposures;
using Base.OrderAccumulator.Domain.Orders;
using Base.OrderAccumulator.Entrypoint.Fix;
using Base.OrderAccumulator.Entrypoint.Workers;
using Base.OrderAccumulator.Entrypoint;
using Base.OrderAccumulator.Infrastructure.Metrics;
using Base.OrderAccumulator.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Primitives;
using Npgsql;

var orderAccumulatorWebBuilder = WebApplication.CreateBuilder(args);

// O /version promete o sha completo; sem ele o app não sobe, para o erro aparecer no build e não no aceite.
var buildCommitSha = ReadBuildCommitSha() is { Length: 40 } shaFromBuild
    ? shaFromBuild
    : throw new InvalidOperationException(
        "O build não gravou o commit. Compile dentro do repositório git ou passe -p:SourceRevisionId=<sha completo>.");

var flowaConnectionString = orderAccumulatorWebBuilder.Configuration.GetConnectionString(OrderAccumulatorConfigurationKeys.OrderDatabaseConnectionStringName)
    ?? throw new InvalidOperationException("Defina ConnectionStrings__Flowa com a conexão do PostgreSQL.");
orderAccumulatorWebBuilder.Services.AddOrderAccumulatorPersistence(flowaConnectionString);
orderAccumulatorWebBuilder.Services.AddOrderMetrics(orderAccumulatorWebBuilder.Configuration);
orderAccumulatorWebBuilder.Services.AddSingleton<SymbolExposureMemoryService>();
orderAccumulatorWebBuilder.Services.TryAddSingleton(TimeProvider.System);
orderAccumulatorWebBuilder.Services.AddScoped<OrderDecisionDomainService>();
orderAccumulatorWebBuilder.Services.AddScoped<DecideIncomingOrderUseCase>();
orderAccumulatorWebBuilder.Services.AddScoped<DeleteAllOrdersUseCase>();
orderAccumulatorWebBuilder.Services.AddScoped<ListOrdersUseCase>();
orderAccumulatorWebBuilder.Services.AddScoped<GetExposuresUseCase>();
orderAccumulatorWebBuilder.Services.AddHostedService<SymbolExposureGaugeWorker>();

// Acceptor FIX 4.4: sobe junto com o app, depois da migração abaixo.
orderAccumulatorWebBuilder.Services.AddSingleton<NewOrderSingleConsumer>();
orderAccumulatorWebBuilder.Services.AddHostedService<FixAcceptorWorker>();

var orderAccumulatorApp = orderAccumulatorWebBuilder.Build();

// As tabelas precisam existir antes de a primeira ordem chegar.
await orderAccumulatorApp.Services.GetRequiredService<NpgsqlDataSource>().ApplyOrderAccumulatorSchemaAsync();
await orderAccumulatorApp.Services.LoadSymbolExposureMemoryAsync();

orderAccumulatorApp.MapGet("/health", () => "Healthy");
orderAccumulatorApp.MapGet("/version", () => new { commit = buildCommitSha });

orderAccumulatorApp.MapGet("/api/exposures", async (GetExposuresUseCase getExposuresUseCase, CancellationToken cancellationToken) =>
{
    var symbolExposures = await getExposuresUseCase.GetExposuresAsync(cancellationToken);
    return new ExposuresResponse(
        ExposureLimitPolicy.PerSymbol,
        symbolExposures.Select(symbolExposure => new SymbolExposureResponse(
            symbolExposure.Symbol, symbolExposure.Exposure, symbolExposure.RemainingExposureCapacity)).ToList());
});

// O teto de páginas limita o custo de um OFFSET grande no banco.
const int MaxOrderListPageNumber = 1000;

orderAccumulatorApp.MapGet("/api/orders", async (HttpRequest orderListRequest, ListOrdersUseCase listOrdersUseCase, CancellationToken cancellationToken) =>
{
    if (!TryReadOrderListPageNumber(orderListRequest.Query["page"], out var orderListPageNumber))
    {
        return Results.Json(
            new
            {
                status = "validation_error",
                message = "Página inválida.",
                errors = new[] { new { field = "page", message = $"A página deve ser um número inteiro de 1 a {MaxOrderListPageNumber}." } }
            },
            statusCode: StatusCodes.Status400BadRequest);
    }

    var storedOrderPage = await listOrdersUseCase.ListOrdersAsync(orderListPageNumber, cancellationToken);
    return Results.Json(new OrderPageResponse(
        orderListPageNumber, OrderListReadRepository.OrdersPerPage, storedOrderPage.TotalStoredOrders,
        storedOrderPage.StoredOrders.Select(ToListedOrderResponse).ToList()));
});

orderAccumulatorApp.MapDelete("/api/orders", async (DeleteAllOrdersUseCase deleteAllOrdersUseCase, CancellationToken cancellationToken) =>
{
    await deleteAllOrdersUseCase.DeleteAllOrdersAsync(cancellationToken);
    return Results.NoContent();
});

orderAccumulatorApp.Run();

// O SDK grava o commit do build na versão informativa ("1.0.0+<sha>").
static string? ReadBuildCommitSha()
{
    var informationalVersion = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    var commitSeparatorIndex = informationalVersion?.IndexOf('+') ?? -1;
    return commitSeparatorIndex >= 0 ? informationalVersion![(commitSeparatorIndex + 1)..] : null;
}

// Sem "page" vale a primeira página; o resto precisa ser um inteiro de 1 a 1000, sem sinal nem espaço.
static bool TryReadOrderListPageNumber(StringValues pageQueryValues, out int orderListPageNumber)
{
    orderListPageNumber = 1;
    if (pageQueryValues.Count == 0)
        return true;

    return pageQueryValues.Count == 1
        && int.TryParse(pageQueryValues[0], NumberStyles.None, CultureInfo.InvariantCulture, out orderListPageNumber)
        && orderListPageNumber is >= 1 and <= MaxOrderListPageNumber;
}

static ListedOrderResponse ToListedOrderResponse(OrderListItem storedOrder) => new(
    storedOrder.ReceivedAt,
    storedOrder.Accepted ? "accepted" : "rejected",
    storedOrder.Symbol,
    ToJsonOrderSideOfStoredOrder(storedOrder.Side),
    storedOrder.Quantity,
    storedOrder.Price,
    storedOrder.OrderId,
    storedOrder.ClOrdId);

// Lado fora de 1/2 só existe em ordem rejeitada que chegou direto pelo FIX; sai como null, igual ao símbolo.
static string? ToJsonOrderSideOfStoredOrder(string storedOrderSide) => storedOrderSide switch
{
    [OrderSideCodes.BuyOrderSideFixCode] => OrderSideCodes.BuyOrderSideJsonCode,
    [OrderSideCodes.SellOrderSideFixCode] => OrderSideCodes.SellOrderSideJsonCode,
    _ => null
};

public partial class Program;
