using System.Net;
using System.Text.Json;
using Base.OrderGenerator.Commons;

namespace Base.OrderGenerator.Infrastructure;

public sealed class OrderAccumulatorHttpClient(HttpClient orderAccumulatorHttpClient) : IOrderAccumulatorHttpClient
{
    // The log category of the HttpClient carries this name; appsettings.json keeps its per-call lines below Warning.
    public const string OrderAccumulatorHttpClientName = "OrderAccumulator";

    public async Task<DataMessage<JsonElement>> GetSymbolExposuresAsync(CancellationToken cancellationToken)
    {
        using var exposuresResponse = await orderAccumulatorHttpClient.GetAsync("/api/exposures", cancellationToken);
        EnsureOrderAccumulatorAnsweredWith(exposuresResponse, HttpStatusCode.OK);
        return await ReadSuccessMessageAsync(exposuresResponse, cancellationToken);
    }

    public async Task<DataMessage<JsonElement>> GetStoredOrdersPageAsync(string? requestedPageNumber, CancellationToken cancellationToken)
    {
        // Only the page goes on: the page size is fixed in the OrderAccumulator and the caller's one is ignored.
        var ordersPagePath = requestedPageNumber is null ? "/api/orders" : $"/api/orders?page={Uri.EscapeDataString(requestedPageNumber)}";
        using var ordersPageResponse = await orderAccumulatorHttpClient.GetAsync(ordersPagePath, cancellationToken);
        if (ordersPageResponse.StatusCode == HttpStatusCode.BadRequest)
            return await ReadInvalidInputMessageAsync(ordersPageResponse, cancellationToken);

        EnsureOrderAccumulatorAnsweredWith(ordersPageResponse, HttpStatusCode.OK);
        return await ReadSuccessMessageAsync(ordersPageResponse, cancellationToken);
    }

    public async Task DeleteAllStoredOrdersAsync(CancellationToken cancellationToken)
    {
        using var ordersDeletionResponse = await orderAccumulatorHttpClient.DeleteAsync("/api/orders", cancellationToken);
        EnsureOrderAccumulatorAnsweredWith(ordersDeletionResponse, HttpStatusCode.NoContent);
    }

    // Any other status means the OrderAccumulator did not do what the route promises; the exception reaches the
    // GlobalErrorHandler, which answers the 503 without passing on what the OrderAccumulator wrote.
    private static void EnsureOrderAccumulatorAnsweredWith(HttpResponseMessage orderAccumulatorResponse, HttpStatusCode expectedHttpStatus)
    {
        if (orderAccumulatorResponse.StatusCode != expectedHttpStatus)
            throw new HttpRequestException(
                $"The OrderAccumulator answered {(int)orderAccumulatorResponse.StatusCode} instead of {(int)expectedHttpStatus}.", null, orderAccumulatorResponse.StatusCode);
    }

    private static async Task<DataMessage<JsonElement>> ReadSuccessMessageAsync(HttpResponseMessage successResponse, CancellationToken cancellationToken)
    {
        using var successBody = await JsonDocument.ParseAsync(await successResponse.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var successMessage = successBody.RootElement;
        return DataMessage<JsonElement>.CreateSuccessMessage(successMessage.GetProperty("data").Clone(), successMessage.GetProperty("message").GetString()!);
    }

    // The 400 of the OrderAccumulator is a problem+json: its detail, errors and code become the same error here.
    private static async Task<DataMessage<JsonElement>> ReadInvalidInputMessageAsync(HttpResponseMessage invalidInputResponse, CancellationToken cancellationToken)
    {
        using var problemBody = await JsonDocument.ParseAsync(await invalidInputResponse.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var invalidInputProblem = problemBody.RootElement;
        var problemType = invalidInputProblem.GetProperty("type").GetString()!;
        var problemErrorMessages = invalidInputProblem.GetProperty("errors").EnumerateArray().Select(problemError => problemError.GetString()!).ToList();
        return DataMessage<JsonElement>.CreateErrorMessage(
            invalidInputProblem.GetProperty("detail").GetString()!, ResultStatus.InvalidInput, problemErrorMessages, problemType[(problemType.LastIndexOf(':') + 1)..]);
    }
}
