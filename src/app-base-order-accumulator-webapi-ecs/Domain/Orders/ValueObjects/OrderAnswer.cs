using Base.OrderAccumulator.Domain.Orders.Entities;

namespace Base.OrderAccumulator.Domain.Orders.ValueObjects;

public sealed record OrderAnswer
{
    public Order AnsweredOrder { get; }
    public bool IsRepeat { get; }

    private OrderAnswer(Order answeredOrder, bool isRepeat)
    {
        ArgumentNullException.ThrowIfNull(answeredOrder);

        AnsweredOrder = answeredOrder;
        IsRepeat = isRepeat;
    }

    public static OrderAnswer AnswerNewOrder(Order answeredOrder) => new(answeredOrder, isRepeat: false);

    public static OrderAnswer RepeatStoredAnswer(Order storedOrder) => new(storedOrder, isRepeat: true);

    public bool ShouldMoveSymbolExposure() => !IsRepeat && AnsweredOrder.Accepted;

    public bool ShouldCountInOrderMetrics() => !IsRepeat;
}
