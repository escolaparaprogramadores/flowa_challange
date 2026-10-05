using Base.OrderAccumulator.Domain.Orders;

namespace Base.OrderAccumulator.Application.Orders.DecideIncomingOrder;

// The answer given to the order, already stored. Repeating the ClOrdID returns exactly this one,
// with IsRepeat = true.
public sealed record DecideIncomingOrderOutput(
    string ClOrdId,
    string OrderId,
    string ExecId,
    string? Symbol,
    char Side,
    decimal Quantity,
    decimal Price,
    bool Accepted,
    string? RejectReason,
    bool IsRepeat)
{
    public static DecideIncomingOrderOutput FromAnsweredOrder(Order answeredOrder, bool isRepeat) => new(
        answeredOrder.ClOrdId, answeredOrder.OrderId, answeredOrder.ExecId, answeredOrder.Symbol, answeredOrder.Side,
        answeredOrder.Quantity, answeredOrder.Price, answeredOrder.Accepted, answeredOrder.RejectReason, isRepeat);
}
