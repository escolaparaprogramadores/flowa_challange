using System.Text.Json;
using Base.OrderGenerator.Application.Orders.Responses;
using Base.OrderGenerator.Application.Orders.UseCases;
using Base.OrderGenerator.Commons.Responses;
using Base.OrderGenerator.Domain.Orders.Enums;
using Base.OrderGenerator.Domain.Orders.ValueObjects;
using Base.OrderGenerator.Entrypoint.ErrorHandling;
using Base.OrderGenerator.Entrypoint.Orders.Requests;

namespace Base.OrderGenerator.Entrypoint.Orders.Endpoints;

public static class OrdersEndpoints
{
    public const string InvalidOrderMessage = "A ordem tem campos inválidos.";
    public const string InvalidOrderErrorCode = "invalid-order";

    extension(WebApplication orderGeneratorApp)
    {
        public void MapOrdersEndpoints()
        {
            orderGeneratorApp.MapPost("/api/orders", PostOrderAsync);
            orderGeneratorApp.MapGet("/api/orders", GetOrdersPageAsync);
            orderGeneratorApp.MapDelete("/api/orders", DeleteAllOrdersAsync);
        }
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
            return DataMessage<SentOrderResponse>.CreateErrorMessage(InvalidOrderMessage, ResultStatus.InvalidInput, orderFieldFormatMessages, InvalidOrderErrorCode)
                .ConvertToHttpResponse();
        }

        var sentOrderMessage = await sendOrderUseCase.SendOrderAsync(orderToSend);
        return sentOrderMessage.ConvertToHttpResponse(sentOrderResult => ConvertToSentOrderResponse(sentOrderResult, orderToSend));
    }

    private static async Task<IResult> GetOrdersPageAsync(HttpRequest ordersPageHttpRequest, ListOrdersUseCase listOrdersUseCase, CancellationToken requestAborted)
    {
        var requestedPageNumber = ordersPageHttpRequest.Query.TryGetValue("page", out var pageQueryValues) ? pageQueryValues.ToString() : null;
        return (await listOrdersUseCase.ListOrdersAsync(requestedPageNumber, requestAborted)).ConvertToHttpResponse();
    }

    private static async Task<IResult> DeleteAllOrdersAsync(DeleteAllOrdersUseCase deleteAllOrdersUseCase, CancellationToken requestAborted)
    {
        var ordersDeletionMessage = await deleteAllOrdersUseCase.DeleteAllOrdersAsync(requestAborted);
        return ordersDeletionMessage.Success ? Results.NoContent() : ordersDeletionMessage.ConvertToHttpResponse();
    }

    private static SentOrderResponse ConvertToSentOrderResponse(SentOrderResult sentOrderResult, OrderToSend sentOrder) => new(
        sentOrderResult.Status == SentOrderStatus.Accepted ? "accepted" : "rejected",
        sentOrderResult.ClOrdId,
        sentOrderResult.OrderId,
        sentOrderResult.ExecId,
        sentOrder.Symbol,
        sentOrder.Side == OrderSide.Buy ? OrderRequestFormatValidator.BuyOrderSideJsonCode : OrderRequestFormatValidator.SellOrderSideJsonCode,
        sentOrder.Quantity,
        sentOrder.Price);

    private static async Task<(string? Symbol, string? Side, string? Quantity, string? Price)> ReadRawOrderFields(HttpRequest orderHttpRequest)
    {
        JsonDocument orderJsonDocument;
        try
        {
            orderJsonDocument = await JsonDocument.ParseAsync(orderHttpRequest.Body, cancellationToken: orderHttpRequest.HttpContext.RequestAborted);
        }
        catch (JsonException)
        {
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
