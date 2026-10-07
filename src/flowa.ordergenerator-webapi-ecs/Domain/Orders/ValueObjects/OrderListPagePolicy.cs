using System.Globalization;

namespace Flowa.OrderGenerator.Domain.Orders.ValueObjects;

public static class OrderListPagePolicy
{
    public const int FirstPageNumber = 1;
    public const int MaxPageNumber = 1000;
    public const int OrdersPerPage = 10;

    public static bool TryReadOrderListPageNumber(string? requestedPageNumber, out int orderListPageNumber)
    {
        orderListPageNumber = FirstPageNumber;
        if (requestedPageNumber is null)
            return true;

        return int.TryParse(requestedPageNumber, NumberStyles.None, CultureInfo.InvariantCulture, out orderListPageNumber)
            && orderListPageNumber is >= FirstPageNumber and <= MaxPageNumber;
    }
}
