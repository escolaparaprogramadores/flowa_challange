using System.Net;
using System.Reflection;
using System.Text.Json;
using Base.OrderGenerator.Application.Orders.SendOrder;
using Base.OrderGenerator.Domain.Orders;
using Flowa.Shared;

namespace Base.OrderGenerator.Entrypoint;

// Rotas HTTP do contrato v1. Recebem, chamam quem faz o trabalho e traduzem o resultado.
public static class OrderGeneratorApiEndpoints
{
    public const string AccumulatorHttpClientName = "OrderAccumulator";

    public const string InvalidOrderMessage = "A ordem tem campos inválidos.";
    public const string AcceptedOrderMessage = "Ordem aceita.";
    public const string RejectedOrderWithoutTextMessage = "Ordem rejeitada.";
    public const string OrderCommunicationMessage = "Não foi possível falar com o OrderAccumulator. Tente de novo em instantes.";
    public const string ExposureCommunicationMessage = "Não foi possível ler a exposição no OrderAccumulator. Tente de novo em instantes.";
    public const string OrdersPageCommunicationMessage = "Não foi possível ler as ordens no OrderAccumulator. Tente de novo em instantes.";
    public const string OrdersDeletionCommunicationMessage = "Não foi possível apagar as ordens no OrderAccumulator. Tente de novo em instantes.";
    public const string UnexpectedErrorMessage = "Erro inesperado ao processar a ordem.";

    public static void MapOrderGeneratorRoutes(this WebApplication orderGeneratorApp, string buildCommitSha)
    {
        orderGeneratorApp.MapPost("/api/orders", PostOrderAsync);
        orderGeneratorApp.MapGet("/api/orders", GetOrdersPage);
        orderGeneratorApp.MapDelete("/api/orders", DeleteAllOrders);
        orderGeneratorApp.MapGet("/api/exposures", GetExposures);
        orderGeneratorApp.MapGet("/health", () => Results.Text("Healthy"));
        orderGeneratorApp.MapGet("/version", () => Results.Json(new { commit = buildCommitSha }));

        // Caminho de API que não existe responde 404; nunca cai no index.html da tela.
        orderGeneratorApp.Map("/api/{**unknownApiPath}", () => Results.NotFound());
    }

    // O SDK grava o commit na versão informativa do assembly ("1.0.0+<sha>") quando compila dentro do git.
    public static string? ReadBuildCommitSha()
    {
        var informationalVersion = typeof(OrderGeneratorApiEndpoints).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var commitSeparatorIndex = informationalVersion?.IndexOf('+') ?? -1;
        return commitSeparatorIndex < 0 ? null : informationalVersion![(commitSeparatorIndex + 1)..];
    }

    public static IResult BuildOrderGeneratorUnexpectedErrorResponse() =>
        Results.Json(new { status = "error", message = UnexpectedErrorMessage }, statusCode: StatusCodes.Status500InternalServerError);

    private static async Task<IResult> PostOrderAsync(HttpRequest orderHttpRequest, SendOrderUseCase sendOrderUseCase)
    {
        var rawOrderFields = await ReadRawOrderFields(orderHttpRequest);
        var orderValidation = OrderValidator.ValidateOrderFromJson(
            rawOrderFields.Symbol, rawOrderFields.Side, rawOrderFields.Quantity, rawOrderFields.Price);
        if (!orderValidation.IsOrderValid)
        {
            return Results.Json(
                new { status = "validation_error", message = InvalidOrderMessage, errors = orderValidation.OrderFieldErrors },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var validOrder = orderValidation.ValidatedOrder!;
        var sentOrderResult = await sendOrderUseCase.SendOrderAsync(validOrder);

        return sentOrderResult.Status switch
        {
            SentOrderStatus.Accepted => Results.Json(BuildOrderResponseBody("accepted", sentOrderResult, validOrder, AcceptedOrderMessage)),
            SentOrderStatus.Rejected => Results.Json(BuildOrderResponseBody("rejected", sentOrderResult, validOrder,
                sentOrderResult.RejectionText ?? RejectedOrderWithoutTextMessage)),
            SentOrderStatus.NoLoggedOnSession or SentOrderStatus.ExecutionReportTimeout => BuildOrderAccumulatorCommunicationErrorResponse(OrderCommunicationMessage),
            _ => BuildOrderGeneratorUnexpectedErrorResponse()
        };
    }

    private static async Task<IResult> GetExposures(IHttpClientFactory accumulatorHttpClientFactory, CancellationToken requestAborted)
    {
        var accumulatorClient = accumulatorHttpClientFactory.CreateClient(AccumulatorHttpClientName);
        try
        {
            using var accumulatorExposuresResponse = await accumulatorClient.GetAsync("/api/exposures", requestAborted);
            if (accumulatorExposuresResponse.StatusCode != HttpStatusCode.OK)
                return BuildOrderAccumulatorCommunicationErrorResponse(ExposureCommunicationMessage);

            var exposuresJson = await accumulatorExposuresResponse.Content.ReadAsStringAsync(requestAborted);
            return Results.Content(exposuresJson, "application/json", statusCode: StatusCodes.Status200OK);
        }
        catch (HttpRequestException)
        {
            return BuildOrderAccumulatorCommunicationErrorResponse(ExposureCommunicationMessage);
        }
        catch (TaskCanceledException) when (!requestAborted.IsCancellationRequested)
        {
            // Cancelamento sem pedido de quem chamou é o timeout de 5 s do HttpClient.
            return BuildOrderAccumulatorCommunicationErrorResponse(ExposureCommunicationMessage);
        }
    }

    private static Task<IResult> GetOrdersPage(HttpRequest ordersPageHttpRequest, IHttpClientFactory accumulatorHttpClientFactory, CancellationToken requestAborted)
    {
        // Só a página segue adiante: o tamanho da página é fixo no accumulator e o do cliente é ignorado.
        var accumulatorOrdersPagePath = ordersPageHttpRequest.Query.TryGetValue("page", out var requestedOrdersPage)
            ? $"/api/orders?page={Uri.EscapeDataString(requestedOrdersPage.ToString())}"
            : "/api/orders";

        return CallOrderAccumulatorOrdersRoute(accumulatorHttpClientFactory, OrdersPageCommunicationMessage, requestAborted, async accumulatorClient =>
        {
            using var accumulatorOrdersPageResponse = await accumulatorClient.GetAsync(accumulatorOrdersPagePath, requestAborted);
            // O 400 de página inválida volta igual, com o corpo validation_error do accumulator.
            if (accumulatorOrdersPageResponse.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.BadRequest))
                return BuildOrderAccumulatorCommunicationErrorResponse(OrdersPageCommunicationMessage);

            var ordersPageJson = await accumulatorOrdersPageResponse.Content.ReadAsStringAsync(requestAborted);
            return Results.Content(ordersPageJson, "application/json", statusCode: (int)accumulatorOrdersPageResponse.StatusCode);
        });
    }

    private static Task<IResult> DeleteAllOrders(IHttpClientFactory accumulatorHttpClientFactory, CancellationToken requestAborted) =>
        CallOrderAccumulatorOrdersRoute(accumulatorHttpClientFactory, OrdersDeletionCommunicationMessage, requestAborted, async accumulatorClient =>
        {
            using var accumulatorOrdersDeletionResponse = await accumulatorClient.DeleteAsync("/api/orders", requestAborted);
            return accumulatorOrdersDeletionResponse.StatusCode == HttpStatusCode.NoContent
                ? Results.NoContent()
                : BuildOrderAccumulatorCommunicationErrorResponse(OrdersDeletionCommunicationMessage);
        });

    private static async Task<IResult> CallOrderAccumulatorOrdersRoute(IHttpClientFactory accumulatorHttpClientFactory, string communicationErrorMessage,
        CancellationToken requestAborted, Func<HttpClient, Task<IResult>> accumulatorOrdersCall)
    {
        var accumulatorClient = accumulatorHttpClientFactory.CreateClient(AccumulatorHttpClientName);
        try
        {
            return await accumulatorOrdersCall(accumulatorClient);
        }
        catch (HttpRequestException)
        {
            return BuildOrderAccumulatorCommunicationErrorResponse(communicationErrorMessage);
        }
        catch (TaskCanceledException) when (!requestAborted.IsCancellationRequested)
        {
            // Cancelamento sem pedido de quem chamou é o timeout de 5 s do HttpClient.
            return BuildOrderAccumulatorCommunicationErrorResponse(communicationErrorMessage);
        }
    }

    private static IResult BuildOrderAccumulatorCommunicationErrorResponse(string communicationErrorMessage) =>
        Results.Json(new { status = "communication_error", message = communicationErrorMessage },
            statusCode: StatusCodes.Status503ServiceUnavailable);

    private static object BuildOrderResponseBody(string orderStatus, SentOrderResult sentOrderResult, ValidOrder validOrder, string orderMessage) => new
    {
        status = orderStatus,
        clOrdId = sentOrderResult.ClOrdId,
        orderId = sentOrderResult.OrderId,
        execId = sentOrderResult.ExecId,
        symbol = validOrder.OrderSymbol,
        side = validOrder.OrderSide.ToJsonOrderSide(),
        quantity = validOrder.OrderQuantity,
        price = validOrder.OrderPrice,
        message = orderMessage
    };

    // Quantidade e preço chegam como texto cru para o validador dar a mensagem certa a "abc" ou 1.5.
    private static async Task<(string? Symbol, string? Side, string? Quantity, string? Price)> ReadRawOrderFields(HttpRequest orderHttpRequest)
    {
        JsonDocument orderJsonDocument;
        try
        {
            orderJsonDocument = await JsonDocument.ParseAsync(orderHttpRequest.Body, cancellationToken: orderHttpRequest.HttpContext.RequestAborted);
        }
        catch (JsonException)
        {
            // Corpo que não é JSON vira ordem vazia: o validador diz o que falta em cada campo.
            return default;
        }

        using (orderJsonDocument)
        {
            var orderJson = orderJsonDocument.RootElement;
            if (orderJson.ValueKind != JsonValueKind.Object)
                return default;

            return (ReadRawOrderFieldText(orderJson, OrderFields.OrderSymbolFieldName),
                ReadRawOrderFieldText(orderJson, OrderFields.OrderSideFieldName),
                ReadRawOrderFieldText(orderJson, OrderFields.OrderQuantityFieldName),
                ReadRawOrderFieldText(orderJson, OrderFields.OrderPriceFieldName));
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
