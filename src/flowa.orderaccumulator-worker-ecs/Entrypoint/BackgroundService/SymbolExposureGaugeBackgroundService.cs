using Flowa.OrderAccumulator.Application.Exposures.Interfaces;
using Flowa.OrderAccumulator.Application.Orders.Interfaces;
using Flowa.OrderAccumulator.Commons.Logging;

namespace Flowa.OrderAccumulator.Entrypoint.BackgroundService;

public sealed class SymbolExposureGaugeBackgroundService : Microsoft.Extensions.Hosting.BackgroundService
{
    public static readonly TimeSpan SymbolExposureGaugeInterval = TimeSpan.FromSeconds(30);

    private readonly IOrderMetricsPort orderMetrics;
    private readonly ISymbolExposureMemoryPort symbolExposureMemory;
    private readonly TimeProvider gaugeClock;
    private readonly IApplicationLogger<SymbolExposureGaugeBackgroundService> gaugeLogger;

    public SymbolExposureGaugeBackgroundService(
        IOrderMetricsPort orderMetrics,
        ISymbolExposureMemoryPort symbolExposureMemory,
        TimeProvider gaugeClock,
        IApplicationLogger<SymbolExposureGaugeBackgroundService> gaugeLogger)
    {
        this.orderMetrics = orderMetrics ?? throw new ArgumentNullException(nameof(orderMetrics));
        this.symbolExposureMemory = symbolExposureMemory ?? throw new ArgumentNullException(nameof(symbolExposureMemory));
        this.gaugeClock = gaugeClock ?? throw new ArgumentNullException(nameof(gaugeClock));
        this.gaugeLogger = gaugeLogger ?? throw new ArgumentNullException(nameof(gaugeLogger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var symbolExposureGaugeTimer = new PeriodicTimer(SymbolExposureGaugeInterval, gaugeClock);
        gaugeLogger.LogInformation("Symbol exposure gauge loop started.", new { IntervalSeconds = SymbolExposureGaugeInterval.TotalSeconds });
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
