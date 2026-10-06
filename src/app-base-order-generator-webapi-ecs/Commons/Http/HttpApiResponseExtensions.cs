using System.Net;
using System.Text.Json;
using Base.OrderGenerator.Commons.Responses;

namespace Base.OrderGenerator.Commons.Http;

public static class HttpApiResponseExtensions
{
    extension(HttpApiResponse httpApiResponse)
    {
        public void EnsureHttpStatusCode(HttpStatusCode expectedHttpStatus)
        {
            if (httpApiResponse.StatusCode != expectedHttpStatus)
                throw new HttpRequestException(
                    $"The {httpApiResponse.ApiName} answered {(int)httpApiResponse.StatusCode} instead of {(int)expectedHttpStatus}.", null, httpApiResponse.StatusCode);
        }

        public DataMessage<JsonElement> ReadSuccessDataMessage()
        {
            if (httpApiResponse.MediaType != "application/json")
                throw new HttpRequestException($"The {httpApiResponse.ApiName} answered 200 without a JSON body.", null, httpApiResponse.StatusCode);

            using var successBodyStream = new MemoryStream(httpApiResponse.Utf8Body);
            using var successBody = JsonDocument.Parse(successBodyStream);
            var successMessage = successBody.RootElement;
            if (successMessage.ValueKind != JsonValueKind.Object
                || !successMessage.TryGetProperty("data", out var successData)
                || !successMessage.TryGetProperty("message", out var successText) || successText.ValueKind != JsonValueKind.String)
                throw new HttpRequestException($"The {httpApiResponse.ApiName} answered 200 outside the DataMessage contract.", null, httpApiResponse.StatusCode);

            return DataMessage<JsonElement>.CreateSuccessMessage(successData.Clone(), successText.GetString()!);
        }

        public DataMessage<JsonElement> ReadInvalidInputDataMessage()
        {
            if (httpApiResponse.MediaType != "application/problem+json")
                throw new HttpRequestException($"The {httpApiResponse.ApiName} answered 400 without a problem+json body.", null, httpApiResponse.StatusCode);

            using var problemBodyStream = new MemoryStream(httpApiResponse.Utf8Body);
            using var problemBody = JsonDocument.Parse(problemBodyStream);
            var invalidInputProblem = problemBody.RootElement;
            if (!invalidInputProblem.TryGetProperty("type", out var problemTypeElement) || problemTypeElement.ValueKind != JsonValueKind.String
                || !invalidInputProblem.TryGetProperty("detail", out var problemDetail) || problemDetail.ValueKind != JsonValueKind.String
                || !invalidInputProblem.TryGetProperty("errors", out var problemErrors) || problemErrors.ValueKind != JsonValueKind.Array)
                throw new HttpRequestException($"The {httpApiResponse.ApiName} answered 400 outside the problem+json contract.", null, httpApiResponse.StatusCode);

            var problemType = problemTypeElement.GetString()!;
            var problemErrorMessages = problemErrors.EnumerateArray().Select(problemError => problemError.GetString() ?? string.Empty).ToList();
            return DataMessage<JsonElement>.CreateErrorMessage(
                problemDetail.GetString()!, ResultStatus.InvalidInput, problemErrorMessages, problemType[(problemType.LastIndexOf(':') + 1)..]);
        }
    }
}
