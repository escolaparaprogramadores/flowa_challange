using Base.OrderAccumulator.Domain.Exposures.Interfaces;
using Base.OrderAccumulator.Domain.Exposures.ValueObjects;
using Base.OrderAccumulator.Domain.Orders.Entities;
using Base.OrderAccumulator.Domain.Orders.ValueObjects;

namespace Base.OrderAccumulator.Domain.DomainServices;

public sealed class OrderDecisionDomainService(IExposureRepository exposureRepository)
{
    public async Task<Order> DecideIncomingOrderAsync(IncomingOrder incomingOrder, CancellationToken cancellationToken = default)
    {
        var orderFieldValidation = OrderFieldPolicy.ValidateIncomingOrderFields(incomingOrder);
        if (orderFieldValidation.ValidOrderFields is not { } validOrderFields)
            return Order.RejectOrderWithInvalidFields(incomingOrder, orderFieldValidation.InvalidOrderFieldMessages);

        var orderFitsExposureLimit = await exposureRepository.TryMoveSymbolExposureWithinLimitAsync(
            validOrderFields.Symbol,
            ExposureLimitPolicy.CalculateOrderExposureDelta(validOrderFields.Side, validOrderFields.Quantity, validOrderFields.Price),
            ExposureLimitPolicy.PerSymbol,
            cancellationToken);

        return orderFitsExposureLimit
            ? Order.AcceptOrder(incomingOrder)
            : Order.RejectOrderOverExposureLimit(incomingOrder, validOrderFields.Symbol);
    }
}
