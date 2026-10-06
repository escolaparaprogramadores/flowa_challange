using Base.OrderGenerator.Domain.Orders.Enums;

namespace Base.OrderGenerator.Domain.Orders.ValueObjects;

public sealed record SentOrderResult(SentOrderStatus Status, string ClOrdId, string? OrderId = null, string? ExecId = null, string? RejectionText = null);
