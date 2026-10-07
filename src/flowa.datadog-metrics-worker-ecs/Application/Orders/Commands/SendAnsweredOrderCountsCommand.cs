namespace Flowa.DatadogMetrics.Application.Orders.Commands;

public sealed record SendAnsweredOrderCountsCommand(long? LastCountedOrderId);
