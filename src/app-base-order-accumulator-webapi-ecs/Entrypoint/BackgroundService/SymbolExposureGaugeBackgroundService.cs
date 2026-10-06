using Base.OrderAccumulator.Application.Exposures.Interfaces;
using Base.OrderAccumulator.Application.Orders.Interfaces;

namespace Base.OrderAccumulator.Entrypoint.BackgroundService;

public sealed class SymbolExposureGaugeBackgroundService(
    IOrderMetricsPort orderMetrics, ISymbolExposureMemoryPort symbolExposureMemory, TimeProvider gaugeClock) : Microsoft.Extensions.Hosting.BackgroundService
{
    public static readonly TimeSpan SymbolExposureGaugeInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var symbolExposureGaugeTimer = new PeriodicTimer(SymbolExposureGaugeInterval, gaugeClock);
        do
        {
            SendSymbolExposureGauges();
        }
        while (await symbolExposureGaugeTimer.WaitForNextTickAsync(stoppingToken));
    }

    public void SendSymbolExposureGauges()
    {
        foreach (var currentSymbolExposure in symbolExposureMemory.ReadCurrentSymbolExposures())
            orderMetrics.SendSymbolExposureGauge(currentSymbolExposure.Symbol, currentSymbolExposure.Exposure);
    }
}
