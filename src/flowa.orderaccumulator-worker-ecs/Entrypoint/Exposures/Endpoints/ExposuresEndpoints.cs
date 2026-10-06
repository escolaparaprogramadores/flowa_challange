using Flowa.OrderAccumulator.Application.Exposures.Responses;
using Flowa.OrderAccumulator.Application.Exposures.UseCases;
using Flowa.OrderAccumulator.Entrypoint.ErrorHandling;

namespace Flowa.OrderAccumulator.Entrypoint.Exposures.Endpoints;

public static class ExposuresEndpoints
{
    public static IEndpointRouteBuilder MapExposuresEndpoints(this IEndpointRouteBuilder orderAccumulatorRoutes)
    {
        orderAccumulatorRoutes.MapGet("/api/exposures", async (GetExposuresUseCase getExposuresUseCase, CancellationToken cancellationToken) =>
            (await getExposuresUseCase.GetExposuresAsync(cancellationToken)).ConvertToHttpResponse(ExposuresResponse.MapFromSymbolExposures));
        return orderAccumulatorRoutes;
    }
}
