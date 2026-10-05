using System.Globalization;
using System.Reflection;
using System.Text.Json.Serialization;
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
using Base.OrderAccumulator.Entrypoint.Errors;
using Base.OrderAccumulator.Infrastructure.Fix;
using Base.OrderAccumulator.Infrastructure.Logging;
using Base.OrderAccumulator.Infrastructure.Metrics;
using Base.OrderAccumulator.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Primitives;
using Npgsql;

var orderAccumulatorWebBuilder = WebApplication.CreateBuilder(args);
orderAccumulatorWebBuilder.AddApplicationLogging();

// /version promises the full sha; without it the app does not start, so the error shows up in the build and not in the acceptance.
var buildCommitSha = ReadBuildCommitSha() is { Length: 40 } shaFromBuild
    ? shaFromBuild
    : throw new InvalidOperationException(
        "The build did not record the commit. Build inside the git repository or pass -p:SourceRevisionId=<full sha>.");

var flowaConnectionString = orderAccumulatorWebBuilder.Configuration.GetConnectionString(OrderAccumulatorConfigurationKeys.OrderDatabaseConnectionStringName)
    ?? throw new InvalidOperationException("Set ConnectionStrings__Flowa to the PostgreSQL connection.");
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

// FIX 4.4 acceptor: starts together with the app, after the migration below.
orderAccumulatorWebBuilder.Services.AddSingleton<FixSessionLogFactory>();
orderAccumulatorWebBuilder.Services.AddSingleton<NewOrderSingleConsumer>();
orderAccumulatorWebBuilder.Services.AddHostedService<FixAcceptorWorker>();

// Success as DataMessage, with the status by name; every error as problem+json with traceId and one log line.
orderAccumulatorWebBuilder.Services.ConfigureHttpJsonOptions(jsonOptions => jsonOptions.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
// The own writer comes before AddProblemDetails: it is the first in line and answers any caller.
orderAccumulatorWebBuilder.Services.AddSingleton<IProblemDetailsWriter, ProblemDetailsForAnyClientWriter>();
orderAccumulatorWebBuilder.Services.AddProblemDetails(problemDetailsOptions => problemDetailsOptions.CustomizeProblemDetails = ApiProblemDetailsExtensions.CompleteProblemDetails);
orderAccumulatorWebBuilder.Services.AddExceptionHandler<GlobalErrorHandler>();

var orderAccumulatorApp = orderAccumulatorWebBuilder.Build();
orderAccumulatorApp.UseExceptionHandler();
// Any 404 under /api, a route that does not exist included, answers the problem+json too.
orderAccumulatorApp.UseStatusCodePages();

// The tables must exist before the first order arrives.
await orderAccumulatorApp.Services.GetRequiredService<NpgsqlDataSource>().ApplyOrderAccumulatorSchemaAsync();
await orderAccumulatorApp.Services.LoadSymbolExposureMemoryAsync();

orderAccumulatorApp.MapGet("/health", () => "Healthy");
orderAccumulatorApp.MapGet("/version", () => new { commit = buildCommitSha });

orderAccumulatorApp.MapGet("/api/exposures", async (GetExposuresUseCase getExposuresUseCase, CancellationToken cancellationToken) =>
    (await getExposuresUseCase.GetExposuresAsync(cancellationToken)).ConvertToHttpResponse(symbolExposures => new ExposuresResponse(
        ExposureLimitPolicy.PerSymbol,
        symbolExposures.Select(symbolExposure => new SymbolExposureResponse(
            symbolExposure.Symbol, symbolExposure.Exposure, symbolExposure.RemainingExposureCapacity)).ToList())));

// The page cap limits the cost of a large OFFSET in the database.
const int MaxOrderListPageNumber = 1000;

orderAccumulatorApp.MapGet("/api/orders", async (HttpRequest orderListRequest, ListOrdersUseCase listOrdersUseCase, CancellationToken cancellationToken) =>
{
    if (!TryReadOrderListPageNumber(orderListRequest.Query["page"], out var orderListPageNumber))
    {
        return DataMessage<OrderPageResponse>.CreateErrorMessage(
            "Página inválida.", ResultStatus.InvalidInput, [$"A página deve ser um número inteiro de 1 a {MaxOrderListPageNumber}."], "invalid-page")
            .ConvertToHttpResponse();
    }

    return (await listOrdersUseCase.ListOrdersAsync(orderListPageNumber, cancellationToken)).ConvertToHttpResponse(storedOrderPage => new OrderPageResponse(
        orderListPageNumber, OrderListReadRepository.OrdersPerPage, storedOrderPage.TotalStoredOrders,
        storedOrderPage.StoredOrders.Select(ToListedOrderResponse).ToList()));
});

orderAccumulatorApp.MapDelete("/api/orders", async (DeleteAllOrdersUseCase deleteAllOrdersUseCase, CancellationToken cancellationToken) =>
{
    await deleteAllOrdersUseCase.DeleteAllOrdersAsync(cancellationToken);
    return Results.NoContent();
});

orderAccumulatorApp.Run();

// The SDK records the build commit in the informational version ("1.0.0+<sha>").
static string? ReadBuildCommitSha()
{
    var informationalVersion = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    var commitSeparatorIndex = informationalVersion?.IndexOf('+') ?? -1;
    return commitSeparatorIndex >= 0 ? informationalVersion![(commitSeparatorIndex + 1)..] : null;
}

// Without "page" the first page applies; anything else must be an integer from 1 to 1000, with no sign or space.
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

// A side outside 1/2 only exists in a rejected order that came straight through FIX; it goes out as null, like the symbol.
static string? ToJsonOrderSideOfStoredOrder(string storedOrderSide) => storedOrderSide switch
{
    [OrderSideCodes.BuyOrderSideFixCode] => "buy",
    [OrderSideCodes.SellOrderSideFixCode] => "sell",
    _ => null
};

public partial class Program;
