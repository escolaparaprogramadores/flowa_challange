using Base.OrderAccumulator.Domain.Exposures;
using Flowa.Shared;

namespace Base.OrderAccumulator.Domain.Orders;

// Decides between accepting and rejecting a new order. The exposure limit is not checked here
// against a value read before: the atomic move in the database answers whether the order fits,
// and this service turns that answer into the accepted or rejected order (CA-30).
public sealed class OrderDecisionDomainService(IExposureRepository exposureRepository)
{
    public async Task<Order> DecideIncomingOrderAsync(IncomingOrder incomingOrder, CancellationToken cancellationToken = default)
    {
        var orderValidation = OrderValidator.ValidateOrderFromFix(
            incomingOrder.Symbol, incomingOrder.Side, incomingOrder.Quantity, incomingOrder.Price);
        if (!orderValidation.IsOrderValid)
            return Order.RejectOrderWithInvalidFields(
                incomingOrder, orderValidation.OrderFieldErrors.Select(orderFieldError => orderFieldError.OrderFieldErrorMessage));

        var validOrder = orderValidation.ValidatedOrder!;
        var orderFitsExposureLimit = await exposureRepository.TryMoveSymbolExposureWithinLimitAsync(
            validOrder.OrderSymbol,
            ExposureLimitPolicy.CalculateOrderExposureDelta(validOrder.OrderSide, validOrder.OrderQuantity, validOrder.OrderPrice),
            ExposureLimitPolicy.PerSymbol,
            cancellationToken);

        return orderFitsExposureLimit
            ? Order.AcceptOrder(incomingOrder)
            : Order.RejectOrderOverExposureLimit(incomingOrder, validOrder.OrderSymbol);
    }
}
