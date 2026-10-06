using Base.OrderAccumulator.Application.Exposures.Interfaces;
using Base.OrderAccumulator.Application.Orders.Interfaces;

namespace Base.OrderAccumulator.Entrypoint.BackgroundService;

public sealed class SymbolExposureGaugeBackgroundService : Microsoft.Extensions.Hosting.BackgroundService
{
    public static readonly TimeSpan SymbolExposureGaugeInterval = TimeSpan.FromSeconds(30);

    private readonly IOrderMetricsPort orderMetrics;
    private readonly ISymbolExposureMemoryPort symbolExposureMemory;
    private readonly TimeProvider gaugeClock;

    public SymbolExposureGaugeBackgroundService(IOrderMetricsPort orderMetrics, ISymbolExposureMemoryPort symbolExposureMemory, TimeProvider gaugeClock)
    {
        this.orderMetrics = orderMetrics ?? throw new ArgumentNullException(nameof(orderMetrics));
        this.symbolExposureMemory = symbolExposureMemory ?? throw new ArgumentNullException(nameof(symbolExposureMemory));
        this.gaugeClock = gaugeClock ?? throw new ArgumentNullException(nameof(gaugeClock));
    }

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
