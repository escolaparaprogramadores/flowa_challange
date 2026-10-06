using Base.OrderAccumulator.Application.Exposures.Responses;
using Base.OrderAccumulator.Application.Exposures.UseCases;
using Base.OrderAccumulator.Entrypoint.ErrorHandling;

namespace Base.OrderAccumulator.Entrypoint.Exposures.Endpoints;

public static class ExposuresEndpoints
{
    public static IEndpointRouteBuilder MapExposuresEndpoints(this IEndpointRouteBuilder orderAccumulatorRoutes)
    {
        orderAccumulatorRoutes.MapGet("/api/exposures", async (GetExposuresUseCase getExposuresUseCase, CancellationToken cancellationToken) =>
            (await getExposuresUseCase.GetExposuresAsync(cancellationToken)).ConvertToHttpResponse(ExposuresResponse.MapFromSymbolExposures));
        return orderAccumulatorRoutes;
    }
}
