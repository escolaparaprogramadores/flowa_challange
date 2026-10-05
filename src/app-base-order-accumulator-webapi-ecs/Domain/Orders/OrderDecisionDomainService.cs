using Base.OrderAccumulator.Domain.Exposures;

namespace Base.OrderAccumulator.Domain.Orders;

// Decides between accepting and rejecting a new order. The exposure limit is not checked here
// against a value read before: the atomic move in the database answers whether the order fits,
// and this service turns that answer into the accepted or rejected order (CA-30).
public sealed class OrderDecisionDomainService(IExposureRepository exposureRepository)
{
    public async Task<Order> DecideIncomingOrderAsync(IncomingOrder incomingOrder, CancellationToken cancellationToken = default)
    {
        var orderFieldValidation = OrderFieldRule.ValidateIncomingOrderFields(incomingOrder);
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
