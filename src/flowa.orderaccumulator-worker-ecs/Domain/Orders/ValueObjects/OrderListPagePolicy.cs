namespace Flowa.OrderAccumulator.Domain.Orders.ValueObjects;

public static class OrderListPagePolicy
{
    public const int FirstPageNumber = 1;
    public const int MaxPageNumber = 1000;

    public static bool IsAllowedPageNumber(int orderListPageNumber) => orderListPageNumber is >= FirstPageNumber and <= MaxPageNumber;
}
