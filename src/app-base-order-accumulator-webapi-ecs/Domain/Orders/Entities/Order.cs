using Base.OrderAccumulator.Commons.Entities;
using Base.OrderAccumulator.Domain.Exposures.ValueObjects;
using Base.OrderAccumulator.Domain.Orders.ValueObjects;

namespace Base.OrderAccumulator.Domain.Orders.Entities;

public sealed class Order : Entity, IAggregateRoot
{
    private const string OrderIdFormat = "N";

    public string ClOrdId { get; }
    public string OrderId => Id.ToString(OrderIdFormat);
    public string ExecId { get; }
    public string? Symbol { get; }
    public char Side { get; }
    public decimal Quantity { get; }
    public decimal Price { get; }
    public bool Accepted { get; }
    public string? RejectReason { get; }

    private Order(IncomingOrder incomingOrder, bool accepted, string? rejectReason)
    {
        ClOrdId = incomingOrder.ClOrdId;
        ExecId = Guid.NewGuid().ToString(OrderIdFormat);
        Symbol = incomingOrder.Symbol;
        Side = incomingOrder.Side;
        Quantity = incomingOrder.Quantity;
        Price = incomingOrder.Price;
        Accepted = accepted;
        RejectReason = rejectReason;
    }

    private Order(
        Guid orderId, string clOrdId, string execId, string? symbol, char side, decimal quantity, decimal price, bool accepted, string? rejectReason)
        : base(orderId)
    {
        ClOrdId = clOrdId;
        ExecId = execId;
        Symbol = symbol;
        Side = side;
        Quantity = quantity;
        Price = price;
        Accepted = accepted;
        RejectReason = rejectReason;
    }

    public static Order AcceptOrder(IncomingOrder incomingOrder)
    {
        ArgumentNullException.ThrowIfNull(incomingOrder);
        return new Order(incomingOrder, accepted: true, rejectReason: null);
    }

    public static Order RejectOrderWithInvalidFields(IncomingOrder incomingOrder, IEnumerable<string> invalidFieldMessages)
    {
        ArgumentNullException.ThrowIfNull(incomingOrder);
        ArgumentNullException.ThrowIfNull(invalidFieldMessages);
        return new Order(incomingOrder, accepted: false, rejectReason: string.Join(' ', invalidFieldMessages));
    }

    public static Order RejectOrderOverExposureLimit(IncomingOrder incomingOrder, string orderSymbol)
    {
        ArgumentNullException.ThrowIfNull(incomingOrder);
        return new Order(incomingOrder, accepted: false, rejectReason: ExposureLimitPolicy.BuildExposureLimitRejectionText(orderSymbol));
    }

    public static Order RestoreOrder(
        string clOrdId, string orderId, string execId, string? symbol, char side, decimal quantity, decimal price, bool accepted, string? rejectReason) =>
        new(Guid.ParseExact(orderId, OrderIdFormat), clOrdId, execId, symbol, side, quantity, price, accepted, rejectReason);

    public bool WasRejectedForInvalidFields() =>
        !Accepted && OrderFieldPolicy.ValidateIncomingOrderFields(new IncomingOrder(ClOrdId, Symbol, Side, Quantity, Price)).ValidOrderFields is null;

    public decimal CalculateLeavesQuantity() => Accepted ? Quantity : 0m;
}
