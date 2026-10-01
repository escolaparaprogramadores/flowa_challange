using Flowa.Shared;
using OrderAccumulator.Exposure;

namespace OrderAccumulator.Tests;

public sealed class ExposureLimitTests
{
    [Fact]
    public void Buy_adds_price_times_quantity()
    {
        Assert.Equal(1_050.00m, ExposureLimit.OrderExposureDelta(OrderSide.Buy, 100, 10.50m));
    }

    [Fact]
    public void Sell_subtracts_price_times_quantity()
    {
        Assert.Equal(-1_050.00m, ExposureLimit.OrderExposureDelta(OrderSide.Sell, 100, 10.50m));
    }

    [Theory]
    [InlineData(1_000)]
    [InlineData(-1_000)]
    public void Remaining_is_the_limit_minus_the_absolute_exposure(int symbolExposure)
    {
        Assert.Equal(ExposureLimit.PerSymbol - 1_000m, ExposureLimit.RemainingExposureCapacity(symbolExposure));
    }

    [Fact]
    public void Rejection_text_matches_the_contract_word_for_word()
    {
        // Texto da tag 58 em docs/contracts/contracts.md, seção 2.
        Assert.Equal(
            "Ordem rejeitada: a exposição de VALE3 passaria do limite de 100.000.000,00.",
            ExposureLimit.ExposureLimitRejectionText("VALE3"));
    }
}
