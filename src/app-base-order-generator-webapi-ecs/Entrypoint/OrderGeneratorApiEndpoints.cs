using System.Reflection;
using System.Text.Json;
using Base.OrderGenerator.Application.Exposures.GetExposures;
using Base.OrderGenerator.Application.Orders.DeleteAllOrders;
using Base.OrderGenerator.Application.Orders.ListOrders;
using Base.OrderGenerator.Application.Orders.SendOrder;
using Base.OrderGenerator.Commons;
using Base.OrderGenerator.Domain.Orders;
using Base.OrderGenerator.Entrypoint.Errors;

namespace Base.OrderGenerator.Entrypoint;

// HTTP routes of the contract (docs/contracts/contracts.md). They take the request, call the use case and answer:
// success as DataMessage, error as problem+json. No error is handled here: the GlobalErrorHandler is the one place.
public static class OrderGeneratorApiEndpoints
{
    public const string InvalidOrderMessage = "A ordem tem campos inválidos.";
    public const string InvalidOrderErrorCode = "invalid-order";

    public static void MapOrderGeneratorRoutes(this WebApplication orderGeneratorApp, string buildCommitSha)
    {
        orderGeneratorApp.MapPost("/api/orders", PostOrderAsync);
        orderGeneratorApp.MapGet("/api/orders", GetOrdersPageAsync);
        orderGeneratorApp.MapDelete("/api/orders", DeleteAllOrdersAsync);
        orderGeneratorApp.MapGet("/api/exposures", GetExposuresAsync);
        orderGeneratorApp.MapGet("/health", () => Results.Text("Healthy"));
        orderGeneratorApp.MapGet("/version", () => Results.Json(new { commit = buildCommitSha }));

        // An API path that does not exist answers the 404 problem; it never falls into the index.html of the screen.
        orderGeneratorApp.Map("/api/{**unknownApiPath}", () => Results.NotFound());
    }

    // The SDK writes the commit into the informational version of the assembly ("1.0.0+<sha>") when it builds inside git.
    public static string? ReadBuildCommitSha()
    {
        var informationalVersion = typeof(OrderGeneratorApiEndpoints).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var commitSeparatorIndex = informationalVersion?.IndexOf('+') ?? -1;
        return commitSeparatorIndex < 0 ? null : informationalVersion![(commitSeparatorIndex + 1)..];
    }

    private static async Task<IResult> PostOrderAsync(HttpRequest orderHttpRequest, SendOrderUseCase sendOrderUseCase)
    {
        var rawOrderFields = await ReadRawOrderFields(orderHttpRequest);
        var orderRequestFormatValidation = OrderRequestFormatValidator.ValidateOrderRequestFormat(
            rawOrderFields.Symbol, rawOrderFields.Side, rawOrderFields.Quantity, rawOrderFields.Price);
        if (orderRequestFormatValidation.OrderToSend is not { } orderToSend)
        {
            var orderFieldFormatMessages = orderRequestFormatValidation.OrderFieldFormatErrors
                .Select(orderFieldFormatError => orderFieldFormatError.OrderFieldFormatMessage).ToList();
            return DataMessage<OrderResponse>.CreateErrorMessage(InvalidOrderMessage, ResultStatus.InvalidInput, orderFieldFormatMessages, InvalidOrderErrorCode)
                .ConvertToHttpResponse();
        }

        var sentOrderMessage = await sendOrderUseCase.SendOrderAsync(orderToSend);
        return sentOrderMessage.ConvertToHttpResponse(sentOrderResult => ConvertToOrderResponse(sentOrderResult, orderToSend));
    }

    private static async Task<IResult> GetExposuresAsync(GetExposuresUseCase getExposuresUseCase, CancellationToken requestAborted) =>
        (await getExposuresUseCase.GetExposuresAsync(requestAborted)).ConvertToHttpResponse();

    private static async Task<IResult> GetOrdersPageAsync(HttpRequest ordersPageHttpRequest, ListOrdersUseCase listOrdersUseCase, CancellationToken requestAborted)
    {
        var requestedPageNumber = ordersPageHttpRequest.Query.TryGetValue("page", out var pageQueryValues) ? pageQueryValues.ToString() : null;
        return (await listOrdersUseCase.ListOrdersAsync(requestedPageNumber, requestAborted)).ConvertToHttpResponse();
    }

    // The contract answers the deletion with 204 and no body: there is no body to put in the envelope (ASSUMI-5).
    private static async Task<IResult> DeleteAllOrdersAsync(DeleteAllOrdersUseCase deleteAllOrdersUseCase, CancellationToken requestAborted)
    {
        var ordersDeletionMessage = await deleteAllOrdersUseCase.DeleteAllOrdersAsync(requestAborted);
        return ordersDeletionMessage.Success ? Results.NoContent() : ordersDeletionMessage.ConvertToHttpResponse();
    }

    private static OrderResponse ConvertToOrderResponse(SentOrderResult sentOrderResult, OrderToSend sentOrder) => new(
        sentOrderResult.Status == SentOrderStatus.Accepted ? "accepted" : "rejected",
        sentOrderResult.ClOrdId,
        sentOrderResult.OrderId,
        sentOrderResult.ExecId,
        sentOrder.Symbol,
        sentOrder.Side == OrderSide.Buy ? OrderRequestFormatValidator.BuyOrderSideJsonCode : OrderRequestFormatValidator.SellOrderSideJsonCode,
        sentOrder.Quantity,
        sentOrder.Price);

    // Quantity and price arrive as raw text so the format check can answer "abc" with a message.
    private static async Task<(string? Symbol, string? Side, string? Quantity, string? Price)> ReadRawOrderFields(HttpRequest orderHttpRequest)
    {
        JsonDocument orderJsonDocument;
        try
        {
            orderJsonDocument = await JsonDocument.ParseAsync(orderHttpRequest.Body, cancellationToken: orderHttpRequest.HttpContext.RequestAborted);
        }
        catch (JsonException)
        {
            // A body that is not JSON becomes an empty order: the format check answers the 400 with what each field is
            // missing, and that 400 has its log line like any other.
            return default;
        }

        using (orderJsonDocument)
        {
            var orderJson = orderJsonDocument.RootElement;
            if (orderJson.ValueKind != JsonValueKind.Object)
                return default;

            return (ReadRawOrderFieldText(orderJson, OrderRequestFormatValidator.OrderSymbolFieldName),
                ReadRawOrderFieldText(orderJson, OrderRequestFormatValidator.OrderSideFieldName),
                ReadRawOrderFieldText(orderJson, OrderRequestFormatValidator.OrderQuantityFieldName),
                ReadRawOrderFieldText(orderJson, OrderRequestFormatValidator.OrderPriceFieldName));
        }
    }

    private static string? ReadRawOrderFieldText(JsonElement orderJson, string orderFieldName)
    {
        if (!orderJson.TryGetProperty(orderFieldName, out var orderFieldValue))
            return null;

        return orderFieldValue.ValueKind switch
        {
            JsonValueKind.String => orderFieldValue.GetString(),
            JsonValueKind.Null => null,
            _ => orderFieldValue.GetRawText()
        };
    }
}

// "data" of the answer of POST /api/orders: the fields the contract promises, with the side as "buy" or "sell".
public sealed record OrderResponse(
    string Status, string ClOrdId, string? OrderId, string? ExecId, string Symbol, string Side, decimal Quantity, decimal Price);
