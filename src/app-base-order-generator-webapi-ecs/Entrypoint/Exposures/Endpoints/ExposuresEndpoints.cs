using Base.OrderGenerator.Application.Exposures.UseCases;
using Base.OrderGenerator.Entrypoint.ErrorHandling;

namespace Base.OrderGenerator.Entrypoint.Exposures.Endpoints;

public static class ExposuresEndpoints
{
    extension(WebApplication orderGeneratorApp)
    {
        public void MapExposuresEndpoints() => orderGeneratorApp.MapGet("/api/exposures", GetExposuresAsync);
    }

    private static async Task<IResult> GetExposuresAsync(GetExposuresUseCase getExposuresUseCase, CancellationToken requestAborted) =>
        (await getExposuresUseCase.GetExposuresAsync(requestAborted)).ConvertToHttpResponse();
}
