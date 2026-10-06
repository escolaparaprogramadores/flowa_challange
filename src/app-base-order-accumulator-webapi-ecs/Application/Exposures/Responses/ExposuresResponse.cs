namespace Base.OrderAccumulator.Entrypoint;

// Body of GET /api/exposures (docs/contracts/contracts.md, section 1). Remaining becomes "remaining" in the JSON.
public sealed record ExposuresResponse(decimal Limit, IReadOnlyList<SymbolExposureResponse> Exposures);

public sealed record SymbolExposureResponse(string Symbol, decimal Exposure, decimal Remaining);

// Body of GET /api/orders: page, pageSize, total and orders in the JSON.
public sealed record OrderPageResponse(int Page, int PageSize, long Total, IReadOnlyList<ListedOrderResponse> Orders);

public sealed record ListedOrderResponse(
    DateTime ReceivedAt, string Status, string? Symbol, string? Side, decimal Quantity, decimal Price, string OrderId, string ClOrdId);
