using System.Net;
using System.Text.Json;
using Base.OrderAccumulator.Application.Orders.ListOrders;
using Microsoft.Extensions.DependencyInjection;

namespace Base.OrderAccumulator.Tests;

// CA-5 and CA-6 on the OrderAccumulator (backend-problem-details.md, "Teste obrigatório do contrato"): each HTTP error
// is a problem+json for any caller, with exactly one log line from the GlobalErrorHandler carrying the same trace id.
[Collection(OrderAccumulatorPostgresCollection.Name)]
public sealed class HttpErrorContractTests(OrderAccumulatorPostgresFixture orderAccumulatorDatabase)
{
    private const string GlobalErrorHandlerCategory = "Base.OrderAccumulator.Entrypoint.Errors.GlobalErrorHandler";
    private const string DatabaseFailureDetail = "Timeout connecting to orders-db-internal-01:5432";

    [Theory]
    [InlineData("application/json")]
    [InlineData("text/html")]
    public async Task Unknown_api_path_answers_404_problem_with_one_warning(string acceptedMediaType)
    {
        using var stdoutJsonLogCapture = new StdoutJsonLogCapture();
        JsonElement notFoundProblem;
        await using (var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor())
        {
            notFoundProblem = await HttpContractAssertions.ReadProblemDetailsAsync(
                await SendGetAcceptingAsync(orderAccumulatorTestApp, "/api/nada", acceptedMediaType), HttpStatusCode.NotFound);
        }

        Assert.Equal("urn:base-investimentos:problem:not-found", notFoundProblem.GetProperty("type").GetString());
        Assert.Equal("Não encontrado", notFoundProblem.GetProperty("title").GetString());
        Assert.Equal("Não encontrado", notFoundProblem.GetProperty("detail").GetString());
        Assert.Equal("NotFound", notFoundProblem.GetProperty("statusResultado").GetString());
        Assert.Empty(notFoundProblem.GetProperty("errors").EnumerateArray());
        var notFoundLine = AssertSingleHttpErrorLine(stdoutJsonLogCapture, "Warning", "Expected error in request.", notFoundProblem);
        Assert.Null(notFoundLine.ReadLogField("Route"));
        Assert.Null(notFoundLine.Exception);
    }

    [Fact]
    public async Task Method_the_route_does_not_have_answers_405_problem_with_one_warning()
    {
        using var stdoutJsonLogCapture = new StdoutJsonLogCapture();
        JsonElement methodNotAllowedProblem;
        await using (var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor())
        {
            methodNotAllowedProblem = await HttpContractAssertions.ReadProblemDetailsAsync(
                await orderAccumulatorTestApp.CreateClient().PostAsync("/api/orders", null), HttpStatusCode.MethodNotAllowed);
        }

        Assert.Equal("urn:base-investimentos:problem:method-not-allowed", methodNotAllowedProblem.GetProperty("type").GetString());
        Assert.Equal("Método não permitido", methodNotAllowedProblem.GetProperty("title").GetString());
        Assert.Equal("Método não permitido", methodNotAllowedProblem.GetProperty("detail").GetString());
        Assert.Equal("InvalidInput", methodNotAllowedProblem.GetProperty("statusResultado").GetString());
        var methodNotAllowedLine = AssertSingleHttpErrorLine(stdoutJsonLogCapture, "Warning", "Expected error in request.", methodNotAllowedProblem);
        Assert.Equal("POST", methodNotAllowedLine.ReadLogField("Method"));
        Assert.Null(methodNotAllowedLine.Exception);
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("text/html")]
    public async Task Unexpected_exception_answers_500_problem_with_one_error_and_no_internal_detail(string acceptedMediaType)
    {
        using var stdoutJsonLogCapture = new StdoutJsonLogCapture();
        HttpResponseMessage unexpectedErrorResponse;
        JsonElement unexpectedErrorProblem;
        await using (var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString,
            replaceOrderAccumulatorServices: testServices => testServices.AddSingleton<IOrderListReadRepository, FailingOrderListReadRepository>()).StartWithFixAcceptor())
        {
            unexpectedErrorResponse = await SendGetAcceptingAsync(orderAccumulatorTestApp, "/api/orders?page=1", acceptedMediaType);
            unexpectedErrorProblem = await HttpContractAssertions.ReadProblemDetailsAsync(unexpectedErrorResponse, HttpStatusCode.InternalServerError);
        }

        Assert.Equal("urn:base-investimentos:problem:internal-error", unexpectedErrorProblem.GetProperty("type").GetString());
        Assert.Equal("Erro interno", unexpectedErrorProblem.GetProperty("title").GetString());
        Assert.Equal("Aconteceu um erro inesperado. Informe o traceId ao suporte.", unexpectedErrorProblem.GetProperty("detail").GetString());
        Assert.Equal("InternalError", unexpectedErrorProblem.GetProperty("statusResultado").GetString());
        Assert.DoesNotContain(DatabaseFailureDetail, await unexpectedErrorResponse.Content.ReadAsStringAsync());
        var unexpectedErrorLine = AssertSingleHttpErrorLine(stdoutJsonLogCapture, "Error", "Unexpected application error.", unexpectedErrorProblem);
        Assert.Equal("/api/orders", unexpectedErrorLine.ReadLogField("Route"));
        Assert.StartsWith($"System.InvalidOperationException: {DatabaseFailureDetail}", unexpectedErrorLine.Exception);
    }

    private static Task<HttpResponseMessage> SendGetAcceptingAsync(OrderAccumulatorFixTestHost orderAccumulatorTestApp, string apiPath, string acceptedMediaType)
    {
        var apiRequest = new HttpRequestMessage(HttpMethod.Get, apiPath);
        apiRequest.Headers.Accept.ParseAdd(acceptedMediaType);
        return orderAccumulatorTestApp.CreateClient().SendAsync(apiRequest);
    }

    // Exactly one Warning or Error line in the whole app, whatever wrote it, so a second log of the same error fails.
    private static JsonLogLine AssertSingleHttpErrorLine(StdoutJsonLogCapture stdoutJsonLogCapture, string expectedLogLevel, string expectedMessage, JsonElement responseProblemDetails)
    {
        var httpErrorLine = Assert.Single(stdoutJsonLogCapture.JsonLogLines, jsonLogLine => jsonLogLine.LogLevel is "Warning" or "Error");
        Assert.Equal((GlobalErrorHandlerCategory, expectedLogLevel, expectedMessage), (httpErrorLine.Category, httpErrorLine.LogLevel, httpErrorLine.Message));
        Assert.Equal(responseProblemDetails.GetProperty("type").GetString(), httpErrorLine.ReadLogField("ErrorCode"));
        Assert.Equal(responseProblemDetails.GetProperty("traceId").GetString(), httpErrorLine.TraceId);
        return httpErrorLine;
    }

    // Only the list of the screen fails, so the app still starts (the exposure memory loads by its own repository).
    private sealed class FailingOrderListReadRepository : IOrderListReadRepository
    {
        public Task<OrderListPage> ReadStoredOrderPageAsync(int pageNumber, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(DatabaseFailureDetail);
    }
}
