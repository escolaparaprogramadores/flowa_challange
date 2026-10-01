using System.Net;
using System.Text.Json;
using Flowa.Shared;

namespace OrderGenerator;

// Rotas HTTP do contrato v1. Recebem, chamam quem faz o trabalho e traduzem o resultado.
public static class ApiEndpoints
{
    public const string AccumulatorClient = "OrderAccumulator";

    public const string InvalidOrderMessage = "A ordem tem campos inválidos.";
    public const string AcceptedMessage = "Ordem aceita.";
    public const string RejectedWithoutTextMessage = "Ordem rejeitada.";
    public const string OrderCommunicationMessage = "Não foi possível falar com o OrderAccumulator. Tente de novo em instantes.";
    public const string ExposureCommunicationMessage = "Não foi possível ler a exposição no OrderAccumulator. Tente de novo em instantes.";
    public const string UnexpectedMessage = "Erro inesperado ao processar a ordem.";

    public static void MapApi(this WebApplication app)
    {
        app.MapPost("/api/orders", PostOrder);
        app.MapGet("/api/exposures", GetExposures);
        app.MapGet("/health", () => Results.Text("Healthy"));

        // Caminho de API que não existe responde 404; nunca cai no index.html da tela.
        app.Map("/api/{**rest}", () => Results.NotFound());
    }

    public static IResult UnexpectedError() =>
        Results.Json(new { status = "error", message = UnexpectedMessage }, statusCode: StatusCodes.Status500InternalServerError);

    private static async Task<IResult> PostOrder(HttpRequest request, FixOrderClient fix)
    {
        var fields = await ReadRawFields(request);
        var validation = OrderValidator.Validate(fields.Symbol, fields.Side, fields.Quantity, fields.Price);
        if (!validation.IsValid)
        {
            return Results.Json(
                new { status = "validation_error", message = InvalidOrderMessage, errors = validation.Errors },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var order = validation.Order!;
        var result = await fix.SendAsync(order);

        return result.Outcome switch
        {
            OrderOutcome.Accepted => Results.Json(OrderBody("accepted", result, order, AcceptedMessage)),
            OrderOutcome.Rejected => Results.Json(OrderBody("rejected", result, order, result.Text ?? RejectedWithoutTextMessage)),
            OrderOutcome.NoSession or OrderOutcome.Timeout => CommunicationError(OrderCommunicationMessage),
            _ => UnexpectedError()
        };
    }

    private static async Task<IResult> GetExposures(IHttpClientFactory clients, CancellationToken cancellationToken)
    {
        var client = clients.CreateClient(AccumulatorClient);
        try
        {
            using var response = await client.GetAsync("/api/exposures", cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
                return CommunicationError(ExposureCommunicationMessage);

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return Results.Content(body, "application/json", statusCode: StatusCodes.Status200OK);
        }
        catch (HttpRequestException)
        {
            return CommunicationError(ExposureCommunicationMessage);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Cancelamento sem pedido de quem chamou é o timeout de 5 s do HttpClient.
            return CommunicationError(ExposureCommunicationMessage);
        }
    }

    private static IResult CommunicationError(string message) =>
        Results.Json(new { status = "communication_error", message }, statusCode: StatusCodes.Status503ServiceUnavailable);

    private static object OrderBody(string status, OrderResult result, ValidOrder order, string message) => new
    {
        status,
        clOrdId = result.ClOrdId,
        orderId = result.OrderId,
        execId = result.ExecId,
        symbol = order.Symbol,
        side = order.Side.ToJson(),
        quantity = order.Quantity,
        price = order.Price,
        message
    };

    // Quantidade e preço chegam como texto cru para o validador dar a mensagem certa a "abc" ou 1.5.
    private static async Task<(string? Symbol, string? Side, string? Quantity, string? Price)> ReadRawFields(HttpRequest request)
    {
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(request.Body, cancellationToken: request.HttpContext.RequestAborted);
        }
        catch (JsonException)
        {
            // Corpo que não é JSON vira ordem vazia: o validador diz o que falta em cada campo.
            return default;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return default;

            return (RawText(root, OrderFields.Symbol), RawText(root, OrderFields.Side),
                RawText(root, OrderFields.Quantity), RawText(root, OrderFields.Price));
        }
    }

    private static string? RawText(JsonElement root, string field)
    {
        if (!root.TryGetProperty(field, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null => null,
            _ => value.GetRawText()
        };
    }
}
