using Base.OrderAccumulator.Domain.Exposures;

namespace Base.OrderAccumulator.Domain.Orders;

// The order as the accumulator answered it: accepted, or rejected with the tag 58 text.
// The answer is stored once per ClOrdID; a repeated ClOrdID gets this same answer back.
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

    // One sentence per invalid field, in the order the validator reports them.
    public static Order RejectOrderWithInvalidFields(IncomingOrder incomingOrder, IEnumerable<string> invalidFieldMessages) =>
        CreateAnsweredOrder(incomingOrder, accepted: false, rejectReason: string.Join(' ', invalidFieldMessages));

    public static Order RejectOrderOverExposureLimit(IncomingOrder incomingOrder, string orderSymbol) =>
        CreateAnsweredOrder(incomingOrder, accepted: false, rejectReason: ExposureLimitPolicy.BuildExposureLimitRejectionText(orderSymbol));

    // A rejected order whose fields pass the field rule can only have been stopped by the exposure limit.
    // Asking the rule again also answers for an order loaded from the database.
    public bool WasRejectedForInvalidFields() =>
        !Accepted && OrderFieldRule.ValidateIncomingOrderFields(new IncomingOrder(ClOrdId, Symbol, Side, Quantity, Price)).ValidOrderFields is null;

    // Only the repository calls this, to load an order that was already answered.
    public static Order RestoreOrder(
        string clOrdId, string orderId, string execId, string? symbol, char side, decimal quantity, decimal price, bool accepted, string? rejectReason) =>
        new(clOrdId, orderId, execId, symbol, side, quantity, price, accepted, rejectReason);

    // OrderID (tag 37) and ExecID (tag 17) are born here, as UUIDs without dashes.
    private static Order CreateAnsweredOrder(IncomingOrder incomingOrder, bool accepted, string? rejectReason) =>
        new(incomingOrder.ClOrdId, CreateOrderOrExecId(), CreateOrderOrExecId(),
            incomingOrder.Symbol, incomingOrder.Side, incomingOrder.Quantity, incomingOrder.Price, accepted, rejectReason);

    private static string CreateOrderOrExecId() => Guid.NewGuid().ToString("N");
}
