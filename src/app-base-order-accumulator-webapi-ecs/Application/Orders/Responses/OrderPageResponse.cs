namespace Base.OrderAccumulator.Application.Orders.Responses;

public sealed record OrderPageResponse(int Page, int PageSize, long Total, IReadOnlyList<ListedOrderResponse> Orders);
