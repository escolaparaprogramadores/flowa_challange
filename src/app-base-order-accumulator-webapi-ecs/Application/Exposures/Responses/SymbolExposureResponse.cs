namespace Base.OrderAccumulator.Application.Exposures.Responses;

public sealed record SymbolExposureResponse(string Symbol, decimal Exposure, decimal Remaining);
