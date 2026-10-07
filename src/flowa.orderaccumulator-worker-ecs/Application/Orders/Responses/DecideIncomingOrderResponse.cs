using Flowa.OrderAccumulator.Domain.Orders.Enums;
using Flowa.OrderAccumulator.Domain.Orders.ValueObjects;

namespace Flowa.OrderAccumulator.Application.Orders.Responses;

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
    public decimal LeavesQuantity { get; init; }
    public OrderDecisionOutcome DecisionOutcome { get; init; }

    public static DecideIncomingOrderResponse MapFromOrderAnswer(OrderAnswer orderAnswer)
    {
        ArgumentNullException.ThrowIfNull(orderAnswer);

        var answeredOrder = orderAnswer.AnsweredOrder;
        return new DecideIncomingOrderResponse(
            answeredOrder.ClOrdId, answeredOrder.OrderId, answeredOrder.ExecId, answeredOrder.Symbol, answeredOrder.Side,
            answeredOrder.Quantity, answeredOrder.Price, answeredOrder.Accepted, answeredOrder.RejectReason,
            answeredOrder.WasRejectedForInvalidFields(), orderAnswer.IsRepeat)
        {
            LeavesQuantity = answeredOrder.CalculateLeavesQuantity(),
            DecisionOutcome = answeredOrder.ClassifyOrderDecision()
        };
    }
}
