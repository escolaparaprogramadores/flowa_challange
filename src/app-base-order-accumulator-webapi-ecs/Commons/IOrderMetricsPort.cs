namespace Base.OrderAccumulator.Commons;

public interface IOrderMetricsPort
{
    void CountAnsweredOrder(string? orderSymbol, char orderSide, bool orderAccepted);

    void SendSymbolExposureGauge(string orderSymbol, decimal symbolExposure);
}
