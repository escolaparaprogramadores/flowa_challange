using Base.OrderAccumulator.Application.Orders.Responses;
using Base.OrderAccumulator.Domain.Exposures.ValueObjects;

namespace Base.OrderAccumulator.Application.Exposures.Interfaces;

public interface ISymbolExposureMemoryPort
{
    Task<DecideIncomingOrderResponse> DecideOrderOutsideDeleteAllAsync(Func<Task<DecideIncomingOrderResponse>> decideIncomingOrder, CancellationToken cancellationToken);

    Task DeleteAllOrdersAndZeroExposuresAsync(Func<Task> deleteAllStoredOrdersAndZeroExposures, CancellationToken cancellationToken);

    void LoadStoredExposures(IEnumerable<SymbolExposure> storedSymbolExposures);

    void ApplyAcceptedOrder(DecideIncomingOrderResponse acceptedOrder);

    IReadOnlyList<SymbolExposure> ReadCurrentSymbolExposures();
}
