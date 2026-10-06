using Base.OrderAccumulator.Domain.Orders.Enums;

namespace Base.OrderAccumulator.Application.Orders.Responses;

public sealed record ListedOrderResponse(
    DateTime ReceivedAt, string Status, string? Symbol, string? Side, decimal Quantity, decimal Price, string OrderId, string ClOrdId)
{
    public const string AcceptedOrderStatus = "accepted";
    public const string RejectedOrderStatus = "rejected";
    public const string BuyOrderSide = "buy";
    public const string SellOrderSide = "sell";

    public static ListedOrderResponse MapFromStoredOrder(StoredOrderResponse storedOrder)
    {
        ArgumentNullException.ThrowIfNull(storedOrder);

        return new ListedOrderResponse(
            storedOrder.ReceivedAt,
            storedOrder.Accepted ? AcceptedOrderStatus : RejectedOrderStatus,
            storedOrder.Symbol,
            MapOrderSide(OrderSideCodes.ConvertStoredFixCodeToOrderSide(storedOrder.Side)),
            storedOrder.Quantity,
            storedOrder.Price,
            storedOrder.OrderId,
            storedOrder.ClOrdId);
    }

    private static string? MapOrderSide(OrderSide? storedOrderSide) => storedOrderSide switch
    {
        OrderSide.Buy => BuyOrderSide,
        OrderSide.Sell => SellOrderSide,
        _ => null
    };
}
