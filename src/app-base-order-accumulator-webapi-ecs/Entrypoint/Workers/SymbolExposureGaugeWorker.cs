using Base.OrderAccumulator.Application.Exposures;
using Base.OrderAccumulator.Commons;

namespace Base.OrderAccumulator.Entrypoint.Workers;

// The gauge disappears from the chart if nobody resends it; that is why the exposure goes out at startup and again
// every 30 s, read from memory and not from the database. The clock comes from outside so the test can advance it.
public sealed class SymbolExposureGaugeWorker(
    IOrderMetricsPort orderMetrics, SymbolExposureMemoryService symbolExposureMemory, TimeProvider gaugeClock) : BackgroundService
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
