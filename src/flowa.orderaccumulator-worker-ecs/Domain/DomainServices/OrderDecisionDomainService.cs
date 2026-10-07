using Flowa.OrderAccumulator.Domain.Exposures.Interfaces;
using Flowa.OrderAccumulator.Domain.Exposures.ValueObjects;
using Flowa.OrderAccumulator.Domain.Orders.Entities;
using Flowa.OrderAccumulator.Domain.Orders.ValueObjects;

namespace Flowa.OrderAccumulator.Domain.DomainServices;

public sealed class OrderDecisionDomainService
{
    private readonly IExposureRepository exposureRepository;

    public OrderDecisionDomainService(IExposureRepository exposureRepository)
    {
        this.exposureRepository = exposureRepository ?? throw new ArgumentNullException(nameof(exposureRepository));
    }

    public async Task<Order> DecideIncomingOrderAsync(IncomingOrder incomingOrder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(incomingOrder);

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
