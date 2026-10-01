namespace OrderAccumulator.Exposure;

// A ordem como chegou pelo FIX, antes de qualquer validação.
public sealed record IncomingOrder(string ClOrdId, string? Symbol, char Side, decimal Quantity, decimal Price);

// A resposta dada à ordem, já gravada no banco. Repetir o ClOrdID devolve exatamente esta,
// com IsRepeat = true.
public sealed record OrderOutcome(
    string ClOrdId,
    string OrderId,
    string ExecId,
    string? Symbol,
    char Side,
    decimal Quantity,
    decimal Price,
    bool Accepted,
    string? RejectReason,
    bool IsRepeat);

public sealed record SymbolExposure(string Symbol, decimal Exposure)
{
    public decimal RemainingExposureCapacity => ExposureLimit.RemainingExposureCapacity(Exposure);
}

public interface IOrderProcessor
{
    // Valida, aplica o limite e grava a ordem numa transação só.
    Task<OrderOutcome> ProcessIncomingOrderAsync(IncomingOrder incomingOrder, CancellationToken cancellationToken = default);
}

public interface IExposureReader
{
    // Os três símbolos, sempre na ordem de OrderRules.AllowedOrderSymbols.
    Task<IReadOnlyList<SymbolExposure>> GetSymbolExposuresAsync(CancellationToken cancellationToken = default);
}
