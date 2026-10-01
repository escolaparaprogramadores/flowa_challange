using System.Net;
using System.Reflection;
using System.Text.Json;
using Flowa.Shared;

namespace OrderGenerator;

// Rotas HTTP do contrato v1. Recebem, chamam quem faz o trabalho e traduzem o resultado.
public static class ApiEndpoints
{
    public const string AccumulatorHttpClientName = "OrderAccumulator";

    public const string InvalidOrderMessage = "A ordem tem campos inválidos.";
    public const string AcceptedOrderMessage = "Ordem aceita.";
    public const string RejectedOrderWithoutTextMessage = "Ordem rejeitada.";
    public const string OrderCommunicationMessage = "Não foi possível falar com o OrderAccumulator. Tente de novo em instantes.";
    public const string ExposureCommunicationMessage = "Não foi possível ler a exposição no OrderAccumulator. Tente de novo em instantes.";
    public const string UnexpectedErrorMessage = "Erro inesperado ao processar a ordem.";

    public static void MapOrderGeneratorRoutes(this WebApplication orderGeneratorApp, string buildCommitSha)
    {
        orderGeneratorApp.MapPost("/api/orders", PostOrder);
        orderGeneratorApp.MapGet("/api/exposures", GetExposures);
        orderGeneratorApp.MapGet("/health", () => Results.Text("Healthy"));
        orderGeneratorApp.MapGet("/version", () => Results.Json(new { commit = buildCommitSha }));

        // Caminho de API que não existe responde 404; nunca cai no index.html da tela.
        orderGeneratorApp.Map("/api/{**unknownApiPath}", () => Results.NotFound());
    }

    // O SDK grava o commit na versão informativa do assembly ("1.0.0+<sha>") quando compila dentro do git.
    public static string? ReadBuildCommitSha()
    {
        var informationalVersion = typeof(ApiEndpoints).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var commitSeparatorIndex = informationalVersion?.IndexOf('+') ?? -1;
        return commitSeparatorIndex < 0 ? null : informationalVersion![(commitSeparatorIndex + 1)..];
    }

    public static IResult UnexpectedErrorResponse() =>
        Results.Json(new { status = "error", message = UnexpectedErrorMessage }, statusCode: StatusCodes.Status500InternalServerError);

    private static async Task<IResult> PostOrder(HttpRequest orderHttpRequest, FixOrderClient fixOrderClient)
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
        var fixOrderResult = await fixOrderClient.SendNewOrderSingleAsync(validOrder);

        return fixOrderResult.Outcome switch
        {
            OrderOutcome.Accepted => Results.Json(OrderResponseBody("accepted", fixOrderResult, validOrder, AcceptedOrderMessage)),
            OrderOutcome.Rejected => Results.Json(OrderResponseBody("rejected", fixOrderResult, validOrder,
                fixOrderResult.RejectionText ?? RejectedOrderWithoutTextMessage)),
            OrderOutcome.NoLoggedOnSession or OrderOutcome.ExecutionReportTimeout => CommunicationErrorResponse(OrderCommunicationMessage),
            _ => UnexpectedErrorResponse()
        };
    }

    private static async Task<IResult> GetExposures(IHttpClientFactory httpClientFactory, CancellationToken requestAborted)
    {
        var accumulatorClient = httpClientFactory.CreateClient(AccumulatorHttpClientName);
        try
        {
            using var accumulatorExposuresResponse = await accumulatorClient.GetAsync("/api/exposures", requestAborted);
            if (accumulatorExposuresResponse.StatusCode != HttpStatusCode.OK)
                return CommunicationErrorResponse(ExposureCommunicationMessage);

            var exposuresJson = await accumulatorExposuresResponse.Content.ReadAsStringAsync(requestAborted);
            return Results.Content(exposuresJson, "application/json", statusCode: StatusCodes.Status200OK);
        }
        catch (HttpRequestException)
        {
            return CommunicationErrorResponse(ExposureCommunicationMessage);
        }
        catch (TaskCanceledException) when (!requestAborted.IsCancellationRequested)
        {
            // Cancelamento sem pedido de quem chamou é o timeout de 5 s do HttpClient.
            return CommunicationErrorResponse(ExposureCommunicationMessage);
        }
    }

    private static IResult CommunicationErrorResponse(string communicationErrorMessage) =>
        Results.Json(new { status = "communication_error", message = communicationErrorMessage },
            statusCode: StatusCodes.Status503ServiceUnavailable);

    private static object OrderResponseBody(string orderStatus, OrderResult fixOrderResult, ValidOrder validOrder, string orderMessage) => new
    {
        status = orderStatus,
        clOrdId = fixOrderResult.ClOrdId,
        orderId = fixOrderResult.OrderId,
        execId = fixOrderResult.ExecId,
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
