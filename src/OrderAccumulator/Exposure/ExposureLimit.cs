using System.Globalization;
using Flowa.Shared;

namespace OrderAccumulator.Exposure;

// Limite do enunciado (D-27): constante do código, por símbolo. Não vem de configuração.
// Quem garante o limite é o UPDATE condicional do banco; aqui ficam o valor e as contas simples.
public static class ExposureLimit
{
    public const decimal PerSymbol = 100_000_000m;

    // Formato brasileiro fixo, sem depender da cultura instalada no container.
    private static readonly NumberFormatInfo BrazilianNumber = new()
    {
        NumberGroupSeparator = ".",
        NumberDecimalSeparator = ",",
        NumberDecimalDigits = 2
    };

    // Compra soma preço × quantidade; venda subtrai.
    public static decimal OrderExposureDelta(OrderSide orderSide, int orderQuantity, decimal orderPrice) =>
        orderSide == OrderSide.Buy ? orderPrice * orderQuantity : -(orderPrice * orderQuantity);

    // Quanto ainda cabe antes de estourar, para qualquer lado.
    public static decimal Remaining(decimal exposure) => PerSymbol - Math.Abs(exposure);

    // Texto da tag 58 combinado no contrato.
    public static string RejectionText(string orderSymbol) =>
        $"Ordem rejeitada: a exposição de {orderSymbol} passaria do limite de {PerSymbol.ToString("N", BrazilianNumber)}.";
}
