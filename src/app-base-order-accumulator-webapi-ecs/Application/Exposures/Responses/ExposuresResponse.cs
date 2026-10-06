namespace Base.OrderAccumulator.Application.Exposures.Responses;

public sealed record ExposuresResponse(decimal Limit, IReadOnlyList<SymbolExposureResponse> Exposures);
