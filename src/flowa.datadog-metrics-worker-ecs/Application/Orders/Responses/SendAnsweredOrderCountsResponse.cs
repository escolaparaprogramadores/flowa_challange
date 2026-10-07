namespace Flowa.DatadogMetrics.Application.Orders.Responses;

public sealed record SendAnsweredOrderCountsResponse(long LastCountedOrderId, long CountedOrders);
