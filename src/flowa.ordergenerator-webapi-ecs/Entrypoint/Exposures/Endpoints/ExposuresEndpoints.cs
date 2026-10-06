using Flowa.OrderGenerator.Application.Exposures.UseCases;
using Flowa.OrderGenerator.Entrypoint.ErrorHandling;

namespace Flowa.OrderGenerator.Entrypoint.Exposures.Endpoints;

public static class ExposuresEndpoints
{
    extension(WebApplication orderGeneratorApp)
    {
        public void MapExposuresEndpoints() => orderGeneratorApp.MapGet("/api/exposures", GetExposuresAsync);
    }

    private static async Task<IResult> GetExposuresAsync(
        HttpContext httpContext, GetExposuresUseCase getExposuresUseCase, DataMessageHttpResponseConverter dataMessageHttpResponseConverter) =>
        dataMessageHttpResponseConverter.ConvertToHttpResponse(await getExposuresUseCase.GetExposuresAsync(httpContext.RequestAborted), httpContext);
}
