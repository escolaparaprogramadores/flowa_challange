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

    // A 200 outside the DataMessage contract (an OrderAccumulator of the version before, during a rolling deploy, or a
    // page that is not JSON) means the route was not answered as promised: the same 503 as any other unanswered call.
    private static async Task<DataMessage<JsonElement>> ReadSuccessMessageAsync(HttpResponseMessage successResponse, CancellationToken cancellationToken)
    {
        if (successResponse.Content.Headers.ContentType?.MediaType != "application/json")
            throw new HttpRequestException("The OrderAccumulator answered 200 without a JSON body.", null, successResponse.StatusCode);

        using var successBody = await JsonDocument.ParseAsync(await successResponse.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var successMessage = successBody.RootElement;
        if (successMessage.ValueKind != JsonValueKind.Object
            || !successMessage.TryGetProperty("data", out var successData)
            || !successMessage.TryGetProperty("message", out var successText) || successText.ValueKind != JsonValueKind.String)
            throw new HttpRequestException("The OrderAccumulator answered 200 outside the DataMessage contract.", null, successResponse.StatusCode);

        return DataMessage<JsonElement>.CreateSuccessMessage(successData.Clone(), successText.GetString()!);
    }

    // The 400 of the OrderAccumulator is a problem+json: its detail, errors and code become the same error here.
    private static async Task<DataMessage<JsonElement>> ReadInvalidInputMessageAsync(HttpResponseMessage invalidInputResponse, CancellationToken cancellationToken)
    {
        if (invalidInputResponse.Content.Headers.ContentType?.MediaType != "application/problem+json")
            throw new HttpRequestException("The OrderAccumulator answered 400 without a problem+json body.", null, invalidInputResponse.StatusCode);

        using var problemBody = await JsonDocument.ParseAsync(await invalidInputResponse.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var invalidInputProblem = problemBody.RootElement;
        if (!invalidInputProblem.TryGetProperty("type", out var problemTypeElement) || problemTypeElement.ValueKind != JsonValueKind.String
            || !invalidInputProblem.TryGetProperty("detail", out var problemDetail) || problemDetail.ValueKind != JsonValueKind.String
            || !invalidInputProblem.TryGetProperty("errors", out var problemErrors) || problemErrors.ValueKind != JsonValueKind.Array)
            throw new HttpRequestException("The OrderAccumulator answered 400 outside the problem+json contract.", null, invalidInputResponse.StatusCode);

        var problemType = problemTypeElement.GetString()!;
        var problemErrorMessages = problemErrors.EnumerateArray().Select(problemError => problemError.GetString() ?? string.Empty).ToList();
        return DataMessage<JsonElement>.CreateErrorMessage(
            problemDetail.GetString()!, ResultStatus.InvalidInput, problemErrorMessages, problemType[(problemType.LastIndexOf(':') + 1)..]);
    }
}
