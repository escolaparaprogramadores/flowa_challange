namespace Flowa.OrderAccumulator.Application.Orders.Responses;

public sealed record AllOrdersDeletedResponse(IReadOnlyList<string> ZeroedExposureSymbols);
