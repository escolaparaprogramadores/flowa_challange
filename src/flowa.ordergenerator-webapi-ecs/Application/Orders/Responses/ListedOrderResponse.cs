namespace Flowa.OrderGenerator.Application.Orders.Responses;

public sealed record ListedOrderResponse(
    DateTime ReceivedAt, string Status, string? Symbol, string? Side, decimal Quantity, decimal Price, string OrderId, string ClOrdId)
{
    public const string AcceptedOrderStatus = "accepted";
    public const string RejectedOrderStatus = "rejected";
    public const string BuyOrderSide = "buy";
    public const string SellOrderSide = "sell";
    public const string BuyOrderSideFixCode = "1";
    public const string SellOrderSideFixCode = "2";

    public static ListedOrderResponse MapFromStoredOrder(StoredOrderResponse storedOrder)
    {
        ArgumentNullException.ThrowIfNull(storedOrder);

        return new ListedOrderResponse(
            storedOrder.ReceivedAt,
            storedOrder.Accepted ? AcceptedOrderStatus : RejectedOrderStatus,
            storedOrder.Symbol,
            MapStoredOrderSideFixCode(storedOrder.Side),
            storedOrder.Quantity,
            storedOrder.Price,
            storedOrder.OrderId,
            storedOrder.ClOrdId);
    }

    private static string? MapStoredOrderSideFixCode(string storedOrderSideFixCode) => storedOrderSideFixCode switch
    {
        BuyOrderSideFixCode => BuyOrderSide,
        SellOrderSideFixCode => SellOrderSide,
        _ => null
    };
}
