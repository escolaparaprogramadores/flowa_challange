using System.Net;

namespace Base.OrderGenerator.Commons.Http;

public sealed record HttpApiResponse(string ApiName, HttpStatusCode StatusCode, string? MediaType, string Body);
