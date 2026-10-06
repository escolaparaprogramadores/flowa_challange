using Base.OrderAccumulator.Domain.Orders.Entities;

namespace Base.OrderAccumulator.Application.Orders.Responses;

public sealed record DecideIncomingOrderResponse(
    string ClOrdId,
    string OrderId,
    string ExecId,
    string? Symbol,
    char Side,
    decimal Quantity,
    decimal Price,
    bool Accepted,
    string? RejectReason,
    bool RejectedForInvalidFields,
    bool IsRepeat)
{
    public static DecideIncomingOrderResponse FromAnsweredOrder(Order answeredOrder, bool isRepeat) => new(
        answeredOrder.ClOrdId, answeredOrder.OrderId, answeredOrder.ExecId, answeredOrder.Symbol, answeredOrder.Side,
        answeredOrder.Quantity, answeredOrder.Price, answeredOrder.Accepted, answeredOrder.RejectReason,
        answeredOrder.WasRejectedForInvalidFields(), isRepeat);
}
