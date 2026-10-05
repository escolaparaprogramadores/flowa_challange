using Base.OrderAccumulator.Domain.Exposures;
using Base.OrderAccumulator.Domain.Orders;

namespace Base.OrderAccumulator.Tests;

public sealed class ExposureLimitTests
{
    [Fact]
    public void Buy_adds_price_times_quantity()
    {
        Assert.Equal(1_050.00m, ExposureLimitPolicy.CalculateOrderExposureDelta(OrderSide.Buy, 100, 10.50m));
    }

    [Fact]
    public void Sell_subtracts_price_times_quantity()
    {
        Assert.Equal(-1_050.00m, ExposureLimitPolicy.CalculateOrderExposureDelta(OrderSide.Sell, 100, 10.50m));
    }

    [Theory]
    [InlineData(1_000)]
    [InlineData(-1_000)]
    public void Remaining_is_the_limit_minus_the_absolute_exposure(int symbolExposure)
    {
        Assert.Equal(ExposureLimitPolicy.PerSymbol - 1_000m, ExposureLimitPolicy.CalculateRemainingExposureCapacity(symbolExposure));
    }

    [Fact]
    public void Rejection_text_matches_the_contract_word_for_word()
    {
        // Texto da tag 58 em docs/contracts/contracts.md, seção 2.
        Assert.Equal(
            "Ordem rejeitada: a exposição de VALE3 passaria do limite de 100.000.000,00.",
            ExposureLimitPolicy.BuildExposureLimitRejectionText("VALE3"));
    }
}
