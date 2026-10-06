using Flowa.OrderAccumulator.Application.Exposures.Interfaces;
using Flowa.OrderAccumulator.Application.Orders.Interfaces;
using Flowa.Commons.Logging;

namespace Flowa.OrderAccumulator.Entrypoint.BackgroundService;

public sealed class SymbolExposureGaugeBackgroundService : Microsoft.Extensions.Hosting.BackgroundService
{
    public static readonly TimeSpan SymbolExposureGaugeInterval = TimeSpan.FromSeconds(30);

    private readonly IOrderMetricsPort orderMetrics;
    private readonly IServiceScopeFactory exposureReadScopeFactory;
    private readonly TimeProvider gaugeClock;
    private readonly IApplicationLogger<SymbolExposureGaugeBackgroundService> gaugeLogger;

    public SymbolExposureGaugeBackgroundService(
        IOrderMetricsPort orderMetrics,
        IServiceScopeFactory exposureReadScopeFactory,
        TimeProvider gaugeClock,
        IApplicationLogger<SymbolExposureGaugeBackgroundService> gaugeLogger)
    {
        this.orderMetrics = orderMetrics ?? throw new ArgumentNullException(nameof(orderMetrics));
        this.exposureReadScopeFactory = exposureReadScopeFactory ?? throw new ArgumentNullException(nameof(exposureReadScopeFactory));
        this.gaugeClock = gaugeClock ?? throw new ArgumentNullException(nameof(gaugeClock));
        this.gaugeLogger = gaugeLogger ?? throw new ArgumentNullException(nameof(gaugeLogger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var symbolExposureGaugeTimer = new PeriodicTimer(SymbolExposureGaugeInterval, gaugeClock);
        gaugeLogger.LogInformation("Symbol exposure gauge loop started.", new { IntervalSeconds = SymbolExposureGaugeInterval.TotalSeconds });
        do
        {
            await SendSymbolExposureGaugesAsync(stoppingToken);
        }
        while (await symbolExposureGaugeTimer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task SendSymbolExposureGaugesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var exposureReadScope = exposureReadScopeFactory.CreateAsyncScope();
            var storedSymbolExposures = await exposureReadScope.ServiceProvider.GetRequiredService<ISymbolExposureReadRepository>()
                .GetSymbolExposuresAsync(cancellationToken);
            foreach (var storedSymbolExposure in storedSymbolExposures)
                orderMetrics.SendSymbolExposureGauge(storedSymbolExposure.Symbol, storedSymbolExposure.Exposure);
        }
        catch (Exception exposureReadFailure) when (!cancellationToken.IsCancellationRequested)
        {
            gaugeLogger.LogError(exposureReadFailure, "Symbol exposure gauge could not read the stored exposures.");
        }
    }
}
