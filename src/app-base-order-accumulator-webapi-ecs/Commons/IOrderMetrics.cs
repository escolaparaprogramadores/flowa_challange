namespace Base.OrderAccumulator.Commons;

public interface IOrderMetrics
{
    void CountAnsweredOrder(string? orderSymbol, char orderSide, bool orderAccepted);

    void SendSymbolExposureGauge(string orderSymbol, decimal symbolExposure);
}
