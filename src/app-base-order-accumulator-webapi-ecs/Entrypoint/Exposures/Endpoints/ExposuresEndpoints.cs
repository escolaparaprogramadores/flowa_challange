using Base.OrderAccumulator.Application.Exposures.Responses;
using Base.OrderAccumulator.Application.Exposures.UseCases;
using Base.OrderAccumulator.Domain.Exposures.ValueObjects;
using Base.OrderAccumulator.Entrypoint.ErrorHandling;

namespace Base.OrderAccumulator.Entrypoint.Exposures.Endpoints;

public static class ExposuresEndpoints
{
    public static IEndpointRouteBuilder MapExposuresEndpoints(this IEndpointRouteBuilder orderAccumulatorRoutes)
    {
        orderAccumulatorRoutes.MapGet("/api/exposures", async (GetExposuresUseCase getExposuresUseCase, CancellationToken cancellationToken) =>
            (await getExposuresUseCase.GetExposuresAsync(cancellationToken)).ConvertToHttpResponse(symbolExposures => new ExposuresResponse(
                ExposureLimitPolicy.PerSymbol,
                symbolExposures.Select(symbolExposure => new SymbolExposureResponse(
                    symbolExposure.Symbol, symbolExposure.Exposure, symbolExposure.RemainingExposureCapacity)).ToList())));
        return orderAccumulatorRoutes;
    }
}
