using System.Globalization;
using System.Reflection;
using Flowa.Shared;
using Microsoft.Extensions.Primitives;
using Npgsql;
using OrderAccumulator.Exposure;
using OrderAccumulator.Fix;
using OrderAccumulator.Observabilidade;
using OrderAccumulator.Persistence;

var orderAccumulatorWebBuilder = WebApplication.CreateBuilder(args);

// O /version promete o sha completo; sem ele o app não sobe, para o erro aparecer no build e não no aceite.
var buildCommitSha = ReadBuildCommitSha() is { Length: 40 } shaFromBuild
    ? shaFromBuild
    : throw new InvalidOperationException(
        "O build não gravou o commit. Compile dentro do repositório git ou passe -p:SourceRevisionId=<sha completo>.");

var flowaConnectionString = orderAccumulatorWebBuilder.Configuration.GetConnectionString("Flowa")
    ?? throw new InvalidOperationException("Defina ConnectionStrings__Flowa com a conexão do PostgreSQL.");
orderAccumulatorWebBuilder.Services.AddOrderAccumulatorPersistence(flowaConnectionString);
orderAccumulatorWebBuilder.Services.AddOrderMetrics(orderAccumulatorWebBuilder.Configuration);

// Acceptor FIX 4.4: sobe junto com o app, depois da migração abaixo.
orderAccumulatorWebBuilder.Services.AddSingleton<OrderFixApplication>();
orderAccumulatorWebBuilder.Services.AddHostedService<FixAcceptorService>();

var orderAccumulatorApp = orderAccumulatorWebBuilder.Build();

// As tabelas precisam existir antes de a primeira ordem chegar.
await orderAccumulatorApp.Services.GetRequiredService<NpgsqlDataSource>().ApplyOrderAccumulatorSchemaAsync();
await orderAccumulatorApp.Services.LoadSymbolExposureMemoryAsync();

orderAccumulatorApp.MapGet("/health", () => "Healthy");
orderAccumulatorApp.MapGet("/version", () => new { commit = buildCommitSha });

orderAccumulatorApp.MapGet("/api/exposures", async (IExposureReader exposureReader, CancellationToken cancellationToken) =>
{
    var symbolExposures = await exposureReader.GetSymbolExposuresAsync(cancellationToken);
    return new ExposuresResponse(
        ExposureLimit.PerSymbol,
        symbolExposures.Select(symbolExposure => new SymbolExposureResponse(
            symbolExposure.Symbol, symbolExposure.Exposure, symbolExposure.RemainingExposureCapacity)).ToList());
});

// O teto de páginas limita o custo de um OFFSET grande no banco (parecer de arquitetura da rodada).
const int MaxOrderListPageNumber = 1000;

orderAccumulatorApp.MapGet("/api/orders", async (HttpRequest orderListRequest, OrderHistoryRepository orderHistoryRepository, CancellationToken cancellationToken) =>
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

    var storedOrderPage = await orderHistoryRepository.ReadStoredOrderPageAsync(orderListPageNumber, cancellationToken);
    return Results.Json(new OrderPageResponse(
        orderListPageNumber, OrderHistoryRepository.OrdersPerPage, storedOrderPage.TotalStoredOrders,
        storedOrderPage.StoredOrders.Select(ToListedOrderResponse).ToList()));
});

orderAccumulatorApp.MapDelete("/api/orders", async (OrderHistoryRepository orderHistoryRepository, SymbolExposureMemory symbolExposureMemory, CancellationToken cancellationToken) =>
{
    // Depois de entrar, o apagar vai até o fim mesmo se o cliente desistir: banco e memória zeram juntos.
    await symbolExposureMemory.DeleteAllOrdersAndZeroExposuresAsync(
        () => orderHistoryRepository.DeleteAllOrdersAndZeroExposuresAsync(CancellationToken.None), cancellationToken);
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

static ListedOrderResponse ToListedOrderResponse(StoredOrderListRow storedOrder) => new(
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

// Corpo do GET /api/exposures (docs/contracts/contracts.md, seção 1). Remaining vira "remaining" no JSON.
public sealed record ExposuresResponse(decimal Limit, IReadOnlyList<SymbolExposureResponse> Exposures);

public sealed record SymbolExposureResponse(string Symbol, decimal Exposure, decimal Remaining);

// Corpo do GET /api/orders: page, pageSize, total e orders no JSON.
public sealed record OrderPageResponse(int Page, int PageSize, long Total, IReadOnlyList<ListedOrderResponse> Orders);

public sealed record ListedOrderResponse(
    DateTime ReceivedAt, string Status, string? Symbol, string? Side, decimal Quantity, decimal Price, string OrderId, string ClOrdId);

public partial class Program;
