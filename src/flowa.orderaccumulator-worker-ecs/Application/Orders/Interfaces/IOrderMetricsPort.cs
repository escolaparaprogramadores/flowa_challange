namespace Flowa.OrderAccumulator.Application.Orders.Interfaces;

public interface IOrderMetricsPort
{
    void CountAnsweredOrder(string? orderSymbol, char orderSide, bool orderAccepted);

    void SendSymbolExposureGauge(string orderSymbol, decimal symbolExposure);
}
