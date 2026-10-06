using System.Net;

namespace Flowa.OrderGenerator.Commons.Http;

public sealed record HttpApiResponse(string ApiName, HttpStatusCode StatusCode, string? MediaType, byte[] Utf8Body);
