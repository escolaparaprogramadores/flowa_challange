using System.Net;
using System.Text.Json;

namespace Flowa.OrderAccumulator.Tests;

// The HTTP contract of the /api routes (CA-4, CA-5), read by the names of the contract, never by the C# types.
public static class HttpContractAssertions
{
    // Every success is a DataMessage with these exact envelope values; "data" is the body of the route.
    public static async Task<JsonElement> ReadSuccessDataMessageAsync(HttpResponseMessage successHttpResponse)
    {
        Assert.Equal(HttpStatusCode.OK, successHttpResponse.StatusCode);
        Assert.Equal("application/json", successHttpResponse.Content.Headers.ContentType?.MediaType);
        var successDataMessage = JsonDocument.Parse(await successHttpResponse.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(
            ["success", "status", "message", "data", "errors", "errorCode"],
            successDataMessage.EnumerateObject().Select(dataMessageField => dataMessageField.Name));
        Assert.True(successDataMessage.GetProperty("success").GetBoolean());
        Assert.Equal("Ok", successDataMessage.GetProperty("status").GetString());
        Assert.Empty(successDataMessage.GetProperty("errors").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, successDataMessage.GetProperty("errorCode").ValueKind);
        return successDataMessage;
    }

    // Every HTTP error is a problem+json whose status matches the HTTP one, with the compatibility fields and the
    // 32 hex trace id of the request.
    public static async Task<JsonElement> ReadProblemDetailsAsync(HttpResponseMessage errorHttpResponse, HttpStatusCode expectedHttpStatus)
    {
        Assert.Equal(expectedHttpStatus, errorHttpResponse.StatusCode);
        Assert.Equal("application/problem+json", errorHttpResponse.Content.Headers.ContentType?.MediaType);
        var responseProblemDetails = JsonDocument.Parse(await errorHttpResponse.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal((int)expectedHttpStatus, responseProblemDetails.GetProperty("status").GetInt32());
        Assert.Equal(errorHttpResponse.RequestMessage!.RequestUri!.AbsolutePath, responseProblemDetails.GetProperty("instance").GetString());
        Assert.Matches("^[0-9a-f]{32}$", responseProblemDetails.GetProperty("traceId").GetString());
        Assert.False(responseProblemDetails.GetProperty("success").GetBoolean());
        return responseProblemDetails;
    }

    public static void AssertInvalidPageProblem(JsonElement invalidPageProblem)
    {
        Assert.Equal("urn:base-investimentos:problem:invalid-page", invalidPageProblem.GetProperty("type").GetString());
        Assert.Equal("Dados inválidos", invalidPageProblem.GetProperty("title").GetString());
        Assert.Equal("Página inválida.", invalidPageProblem.GetProperty("detail").GetString());
        Assert.Equal("InvalidInput", invalidPageProblem.GetProperty("statusResultado").GetString());
        Assert.Equal(
            ["A página deve ser um número inteiro de 1 a 1000."],
            invalidPageProblem.GetProperty("errors").EnumerateArray().Select(problemErrorMessage => problemErrorMessage.GetString()));
    }
}
