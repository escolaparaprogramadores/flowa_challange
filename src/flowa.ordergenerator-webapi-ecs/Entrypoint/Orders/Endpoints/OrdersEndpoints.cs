using System.Text.Json;
using Flowa.OrderGenerator.Application.Orders.UseCases;
using Flowa.OrderGenerator.Domain.Orders.ValueObjects;
using Flowa.OrderGenerator.Entrypoint.ErrorHandling;
using Flowa.OrderGenerator.Entrypoint.Orders.Requests;

namespace Flowa.OrderGenerator.Entrypoint.Orders.Endpoints;

public static class OrdersEndpoints
{
    extension(WebApplication orderGeneratorApp)
    {
        public void MapOrdersEndpoints()
        {
            orderGeneratorApp.MapPost("/api/orders", PostOrderAsync);
            orderGeneratorApp.MapGet("/api/orders", GetOrdersPageAsync);
            orderGeneratorApp.MapDelete("/api/orders", DeleteAllOrdersAsync);
        }
    }

    private static async Task<IResult> PostOrderAsync(
        HttpContext httpContext, SendOrderUseCase sendOrderUseCase, DataMessageHttpResponseConverter dataMessageHttpResponseConverter)
    {
        var sendOrderRequest = await ReadSendOrderRequestAsync(httpContext.Request);
        var sentOrderMessage = await sendOrderUseCase.SendOrderAsync(sendOrderRequest.MapToSendOrderCommand());
        return dataMessageHttpResponseConverter.ConvertToHttpResponse(sentOrderMessage, httpContext);
    }

    private static async Task<IResult> GetOrdersPageAsync(
        HttpContext httpContext, ListOrdersUseCase listOrdersUseCase, DataMessageHttpResponseConverter dataMessageHttpResponseConverter)
    {
        var requestedPageNumber = httpContext.Request.Query.TryGetValue("page", out var pageQueryValues) ? pageQueryValues.ToString() : null;
        var storedOrdersPage = await listOrdersUseCase.ListOrdersAsync(requestedPageNumber, httpContext.RequestAborted);
        return dataMessageHttpResponseConverter.ConvertToHttpResponse(storedOrdersPage, httpContext);
    }

    private static async Task<IResult> DeleteAllOrdersAsync(
        HttpContext httpContext, DeleteAllOrdersUseCase deleteAllOrdersUseCase, DataMessageHttpResponseConverter dataMessageHttpResponseConverter)
    {
        var ordersDeletionMessage = await deleteAllOrdersUseCase.DeleteAllOrdersAsync(httpContext.RequestAborted);
        return ordersDeletionMessage.Success ? Results.NoContent() : dataMessageHttpResponseConverter.ConvertToHttpResponse(ordersDeletionMessage, httpContext);
    }

    private static async Task<SendOrderRequest> ReadSendOrderRequestAsync(HttpRequest orderHttpRequest)
    {
        JsonDocument orderJsonDocument;
        try
        {
            orderJsonDocument = await JsonDocument.ParseAsync(orderHttpRequest.Body, cancellationToken: orderHttpRequest.HttpContext.RequestAborted);
        }
        catch (JsonException)
        {
            return new SendOrderRequest(null, null, null, null);
        }

        using (orderJsonDocument)
        {
            var orderJson = orderJsonDocument.RootElement;
            if (orderJson.ValueKind != JsonValueKind.Object)
                return new SendOrderRequest(null, null, null, null);

            return new SendOrderRequest(
                ReadRawOrderFieldText(orderJson, OrderToSend.OrderSymbolFieldName),
                ReadRawOrderFieldText(orderJson, OrderToSend.OrderSideFieldName),
                ReadRawOrderFieldText(orderJson, OrderToSend.OrderQuantityFieldName),
                ReadRawOrderFieldText(orderJson, OrderToSend.OrderPriceFieldName));
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
