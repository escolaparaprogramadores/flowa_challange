using Base.OrderAccumulator.Commons.Entities;
using Base.OrderAccumulator.Domain.Exposures.ValueObjects;
using Base.OrderAccumulator.Domain.Orders.ValueObjects;

namespace Base.OrderAccumulator.Domain.Orders.Entities;

public sealed class Order : IAggregateRoot
{
    public string ClOrdId { get; private set; }
    public string OrderId { get; private set; }
    public string ExecId { get; private set; }
    public string? Symbol { get; private set; }
    public char Side { get; private set; }
    public decimal Quantity { get; private set; }
    public decimal Price { get; private set; }
    public bool Accepted { get; private set; }
    public string? RejectReason { get; private set; }

    private Order(
        string clOrdId, string orderId, string execId, string? symbol, char side, decimal quantity, decimal price, bool accepted, string? rejectReason)
    {
        ClOrdId = clOrdId;
        OrderId = orderId;
        ExecId = execId;
        Symbol = symbol;
        Side = side;
        Quantity = quantity;
        Price = price;
        Accepted = accepted;
        RejectReason = rejectReason;
    }

    public static Order AcceptOrder(IncomingOrder incomingOrder) => CreateAnsweredOrder(incomingOrder, accepted: true, rejectReason: null);

    public static Order RejectOrderWithInvalidFields(IncomingOrder incomingOrder, IEnumerable<string> invalidFieldMessages) =>
        CreateAnsweredOrder(incomingOrder, accepted: false, rejectReason: string.Join(' ', invalidFieldMessages));

    public static Order RejectOrderOverExposureLimit(IncomingOrder incomingOrder, string orderSymbol) =>
        CreateAnsweredOrder(incomingOrder, accepted: false, rejectReason: ExposureLimitPolicy.BuildExposureLimitRejectionText(orderSymbol));

    public bool WasRejectedForInvalidFields() =>
        !Accepted && OrderFieldPolicy.ValidateIncomingOrderFields(new IncomingOrder(ClOrdId, Symbol, Side, Quantity, Price)).ValidOrderFields is null;

    public static Order RestoreOrder(
        string clOrdId, string orderId, string execId, string? symbol, char side, decimal quantity, decimal price, bool accepted, string? rejectReason) =>
        new(clOrdId, orderId, execId, symbol, side, quantity, price, accepted, rejectReason);

    private static Order CreateAnsweredOrder(IncomingOrder incomingOrder, bool accepted, string? rejectReason) =>
        new(incomingOrder.ClOrdId, CreateOrderOrExecId(), CreateOrderOrExecId(),
            incomingOrder.Symbol, incomingOrder.Side, incomingOrder.Quantity, incomingOrder.Price, accepted, rejectReason);

    private static string CreateOrderOrExecId() => Guid.NewGuid().ToString("N");
}
