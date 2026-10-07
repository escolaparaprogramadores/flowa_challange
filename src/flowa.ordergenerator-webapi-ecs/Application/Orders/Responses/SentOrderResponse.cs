using Flowa.OrderGenerator.Domain.Orders.ValueObjects;

namespace Flowa.OrderGenerator.Application.Orders.Responses;

public sealed record SentOrderResponse(
    string Status, string ClOrdId, string? OrderId, string? ExecId, string Symbol, string Side, decimal Quantity, decimal Price)
{
    public static SentOrderResponse MapFromSentOrder(SentOrderResult sentOrderResult, OrderToSend sentOrder)
    {
        ArgumentNullException.ThrowIfNull(sentOrderResult);
        ArgumentNullException.ThrowIfNull(sentOrder);

        return new SentOrderResponse(
            sentOrderResult.DescribeOrderStatusCode(),
            sentOrderResult.ClOrdId,
            sentOrderResult.OrderId,
            sentOrderResult.ExecId,
            sentOrder.Symbol,
            sentOrder.DescribeOrderSideCode(),
            sentOrder.Quantity,
            sentOrder.Price);
    }
}
