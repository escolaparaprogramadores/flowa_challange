using System.Globalization;
using Base.OrderAccumulator.Domain.Orders;

namespace Base.OrderAccumulator.Domain.Exposures;

// Limit from the challenge statement (D-27): a code constant, per symbol. It does not come from configuration.
// The conditional UPDATE in the database is what enforces the limit; here live the value and the simple calculations.
public static class ExposureLimitPolicy
{
    public const decimal PerSymbol = 100_000_000m;

    // Fixed Brazilian format, not depending on the culture installed in the container.
    private static readonly NumberFormatInfo BrazilianMoneyFormat = new()
    {
        NumberGroupSeparator = ".",
        NumberDecimalSeparator = ",",
        NumberDecimalDigits = 2
    };

    // A buy adds price × quantity; a sell subtracts.
    public static decimal CalculateOrderExposureDelta(OrderSide orderSide, int orderQuantity, decimal orderPrice) =>
        orderSide == OrderSide.Buy ? orderPrice * orderQuantity : -(orderPrice * orderQuantity);

    // How much still fits before the limit is exceeded, on either side.
    public static decimal CalculateRemainingExposureCapacity(decimal symbolExposure) => PerSymbol - Math.Abs(symbolExposure);

    // Tag 58 text agreed in the contract.
    public static string BuildExposureLimitRejectionText(string orderSymbol) =>
        $"Ordem rejeitada: a exposição de {orderSymbol} passaria do limite de {PerSymbol.ToString("N", BrazilianMoneyFormat)}.";
}
