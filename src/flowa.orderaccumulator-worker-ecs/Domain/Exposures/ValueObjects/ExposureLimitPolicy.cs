using System.Globalization;
using Flowa.OrderAccumulator.Domain.Orders.Enums;

namespace Flowa.OrderAccumulator.Domain.Exposures.ValueObjects;

public static class ExposureLimitPolicy
{
    public const decimal PerSymbol = 100_000_000m;

    private static readonly NumberFormatInfo BrazilianMoneyFormat = new()
    {
        NumberGroupSeparator = ".",
        NumberDecimalSeparator = ",",
        NumberDecimalDigits = 2
    };

    public static decimal CalculateOrderExposureDelta(OrderSide orderSide, int orderQuantity, decimal orderPrice) =>
        orderSide == OrderSide.Buy ? orderPrice * orderQuantity : -(orderPrice * orderQuantity);

    public static string BuildExposureLimitRejectionText(string orderSymbol) =>
        $"Ordem rejeitada: a exposição de {orderSymbol} passaria do limite de {PerSymbol.ToString("N", BrazilianMoneyFormat)}.";
}
