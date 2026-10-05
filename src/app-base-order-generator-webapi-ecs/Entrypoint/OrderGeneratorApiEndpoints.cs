using System.Net;
using System.Reflection;
using System.Text.Json;
using Base.OrderGenerator.Application.Orders.SendOrder;
using Base.OrderGenerator.Commons;
using Base.OrderGenerator.Domain.Orders;

namespace Base.OrderGenerator.Entrypoint;

// Rotas HTTP do contrato v1. Recebem, chamam quem faz o trabalho e traduzem o resultado.
public static class OrderGeneratorApiEndpoints
{
    public const string AccumulatorHttpClientName = "OrderAccumulator";

    private const string CommunicationErrorCode = "communication_error";

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
        var orderRequestFormatValidation = OrderRequestFormatValidator.ValidateOrderRequestFormat(
            rawOrderFields.Symbol, rawOrderFields.Side, rawOrderFields.Quantity, rawOrderFields.Price);
        if (orderRequestFormatValidation.OrderToSend is not { } orderToSend)
        {
            return Results.Json(
                new { status = "validation_error", message = InvalidOrderMessage, errors = orderRequestFormatValidation.OrderFieldFormatErrors },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var sentOrderResult = await sendOrderUseCase.SendOrderAsync(orderToSend);

        return sentOrderResult.Status switch
        {
            SentOrderStatus.Accepted => Results.Json(BuildOrderResponseBody("accepted", sentOrderResult, orderToSend, AcceptedOrderMessage)),
            SentOrderStatus.Rejected => Results.Json(BuildOrderResponseBody("rejected", sentOrderResult, orderToSend,
                sentOrderResult.RejectionText ?? RejectedOrderWithoutTextMessage)),
            SentOrderStatus.NoLoggedOnSession or SentOrderStatus.ExecutionReportTimeout => BuildOrderAccumulatorCommunicationErrorResponse(OrderCommunicationMessage),
            _ => BuildOrderGeneratorUnexpectedErrorResponse()
        };
    }

    private static async Task<IResult> GetExposures(
        HttpRequest exposuresHttpRequest, IHttpClientFactory accumulatorHttpClientFactory, IApplicationLogger<Program> forwardedCallLogger, CancellationToken requestAborted)
    {
        var accumulatorClient = accumulatorHttpClientFactory.CreateClient(AccumulatorHttpClientName);
        try
        {
            using var accumulatorExposuresResponse = await accumulatorClient.GetAsync("/api/exposures", requestAborted);
            if (accumulatorExposuresResponse.StatusCode != HttpStatusCode.OK)
                return RespondOrderAccumulatorUnavailable(forwardedCallLogger, exposuresHttpRequest, ExposureCommunicationMessage);

            var exposuresJson = await accumulatorExposuresResponse.Content.ReadAsStringAsync(requestAborted);
            return Results.Content(exposuresJson, "application/json", statusCode: StatusCodes.Status200OK);
        }
        catch (HttpRequestException)
        {
            return RespondOrderAccumulatorUnavailable(forwardedCallLogger, exposuresHttpRequest, ExposureCommunicationMessage);
        }
        catch (TaskCanceledException) when (!requestAborted.IsCancellationRequested)
        {
            // Cancelamento sem pedido de quem chamou é o timeout de 5 s do HttpClient.
            return RespondOrderAccumulatorUnavailable(forwardedCallLogger, exposuresHttpRequest, ExposureCommunicationMessage);
        }
    }

    private static Task<IResult> GetOrdersPage(
        HttpRequest ordersPageHttpRequest, IHttpClientFactory accumulatorHttpClientFactory, IApplicationLogger<Program> forwardedCallLogger, CancellationToken requestAborted)
    {
        // Só a página segue adiante: o tamanho da página é fixo no accumulator e o do cliente é ignorado.
        var accumulatorOrdersPagePath = ordersPageHttpRequest.Query.TryGetValue("page", out var requestedOrdersPage)
            ? $"/api/orders?page={Uri.EscapeDataString(requestedOrdersPage.ToString())}"
            : "/api/orders";

        return CallOrderAccumulatorOrdersRoute(ordersPageHttpRequest, accumulatorHttpClientFactory, forwardedCallLogger, OrdersPageCommunicationMessage, requestAborted, async accumulatorClient =>
        {
            using var accumulatorOrdersPageResponse = await accumulatorClient.GetAsync(accumulatorOrdersPagePath, requestAborted);
            // O 400 de página inválida volta igual, com o corpo validation_error do accumulator.
            if (accumulatorOrdersPageResponse.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.BadRequest))
                return RespondOrderAccumulatorUnavailable(forwardedCallLogger, ordersPageHttpRequest, OrdersPageCommunicationMessage);

            var ordersPageJson = await accumulatorOrdersPageResponse.Content.ReadAsStringAsync(requestAborted);
            return Results.Content(ordersPageJson, "application/json", statusCode: (int)accumulatorOrdersPageResponse.StatusCode);
        });
    }

    private static Task<IResult> DeleteAllOrders(
        HttpRequest ordersDeletionHttpRequest, IHttpClientFactory accumulatorHttpClientFactory, IApplicationLogger<Program> forwardedCallLogger, CancellationToken requestAborted) =>
        CallOrderAccumulatorOrdersRoute(ordersDeletionHttpRequest, accumulatorHttpClientFactory, forwardedCallLogger, OrdersDeletionCommunicationMessage, requestAborted, async accumulatorClient =>
        {
            using var accumulatorOrdersDeletionResponse = await accumulatorClient.DeleteAsync("/api/orders", requestAborted);
            return accumulatorOrdersDeletionResponse.StatusCode == HttpStatusCode.NoContent
                ? Results.NoContent()
                : RespondOrderAccumulatorUnavailable(forwardedCallLogger, ordersDeletionHttpRequest, OrdersDeletionCommunicationMessage);
        });

    private static async Task<IResult> CallOrderAccumulatorOrdersRoute(HttpRequest forwardedHttpRequest, IHttpClientFactory accumulatorHttpClientFactory,
        IApplicationLogger<Program> forwardedCallLogger, string communicationErrorMessage, CancellationToken requestAborted, Func<HttpClient, Task<IResult>> accumulatorOrdersCall)
    {
        var accumulatorClient = accumulatorHttpClientFactory.CreateClient(AccumulatorHttpClientName);
        try
        {
            return await accumulatorOrdersCall(accumulatorClient);
        }
        catch (HttpRequestException)
        {
            return RespondOrderAccumulatorUnavailable(forwardedCallLogger, forwardedHttpRequest, communicationErrorMessage);
        }
        catch (TaskCanceledException) when (!requestAborted.IsCancellationRequested)
        {
            // Cancelamento sem pedido de quem chamou é o timeout de 5 s do HttpClient.
            return RespondOrderAccumulatorUnavailable(forwardedCallLogger, forwardedHttpRequest, communicationErrorMessage);
        }
    }

    // Each 503 of a route that forwards to the OrderAccumulator has exactly one log: a Warning, because the
    // OrderAccumulator being down or slow is an expected error. The 503 of POST /api/orders is logged where the
    // order is sent, inside the order span (FixOrderClient).
    private static IResult RespondOrderAccumulatorUnavailable(IApplicationLogger<Program> forwardedCallLogger, HttpRequest forwardedHttpRequest, string communicationErrorMessage)
    {
        forwardedCallLogger.LogWarning("The OrderAccumulator did not answer the forwarded call.",
            new { ErrorCode = CommunicationErrorCode, Method = forwardedHttpRequest.Method, Route = (forwardedHttpRequest.HttpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText });
        return BuildOrderAccumulatorCommunicationErrorResponse(communicationErrorMessage);
    }

    private static IResult BuildOrderAccumulatorCommunicationErrorResponse(string communicationErrorMessage) =>
        Results.Json(new { status = CommunicationErrorCode, message = communicationErrorMessage },
            statusCode: StatusCodes.Status503ServiceUnavailable);

    private static object BuildOrderResponseBody(string orderStatus, SentOrderResult sentOrderResult, OrderToSend sentOrder, string orderMessage) => new
    {
        status = orderStatus,
        clOrdId = sentOrderResult.ClOrdId,
        orderId = sentOrderResult.OrderId,
        execId = sentOrderResult.ExecId,
        symbol = sentOrder.Symbol,
        side = sentOrder.Side == OrderSide.Buy ? OrderRequestFormatValidator.BuyOrderSideJsonCode : OrderRequestFormatValidator.SellOrderSideJsonCode,
        quantity = sentOrder.Quantity,
        price = sentOrder.Price,
        message = orderMessage
    };

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
            // A body that is not JSON becomes an empty order: the format check reports what each field is missing.
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
