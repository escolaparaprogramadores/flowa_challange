using Flowa.OrderAccumulator.Application.Orders.Responses;
using Flowa.OrderAccumulator.Domain.Exposures.ValueObjects;

namespace Flowa.OrderAccumulator.Application.Exposures.Interfaces;

public interface ISymbolExposureMemoryPort
{
    Task<TOrderDecision> DecideOrderOutsideDeleteAllAsync<TOrderDecision>(Func<Task<TOrderDecision>> decideIncomingOrder, CancellationToken cancellationToken);

    Task DeleteAllOrdersAndZeroExposuresAsync(Func<Task> deleteAllStoredOrdersAndZeroExposures, CancellationToken cancellationToken);

    void LoadStoredExposures(IEnumerable<SymbolExposure> storedSymbolExposures);

    void ApplyAcceptedOrder(DecideIncomingOrderResponse acceptedOrder);

    IReadOnlyList<SymbolExposure> ReadCurrentSymbolExposures();
}
