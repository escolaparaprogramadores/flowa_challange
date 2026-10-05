using System.Globalization;
using Base.OrderAccumulator.Domain.Orders;

namespace Base.OrderAccumulator.Domain.Exposures;

// Limite do enunciado (D-27): constante do código, por símbolo. Não vem de configuração.
// Quem garante o limite é o UPDATE condicional do banco; aqui ficam o valor e as contas simples.
public static class ExposureLimitPolicy
{
    public const decimal PerSymbol = 100_000_000m;

    // Formato brasileiro fixo, sem depender da cultura instalada no container.
    private static readonly NumberFormatInfo BrazilianMoneyFormat = new()
    {
        NumberGroupSeparator = ".",
        NumberDecimalSeparator = ",",
        NumberDecimalDigits = 2
    };

    // Compra soma preço × quantidade; venda subtrai.
    public static decimal CalculateOrderExposureDelta(OrderSide orderSide, int orderQuantity, decimal orderPrice) =>
        orderSide == OrderSide.Buy ? orderPrice * orderQuantity : -(orderPrice * orderQuantity);

    // Quanto ainda cabe antes de estourar, para qualquer lado.
    public static decimal CalculateRemainingExposureCapacity(decimal symbolExposure) => PerSymbol - Math.Abs(symbolExposure);

    // Texto da tag 58 combinado no contrato.
    public static string BuildExposureLimitRejectionText(string orderSymbol) =>
        $"Ordem rejeitada: a exposição de {orderSymbol} passaria do limite de {PerSymbol.ToString("N", BrazilianMoneyFormat)}.";
}
