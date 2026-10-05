using StatsdClient;

namespace OrderAccumulator.Observabilidade;

// O gauge some do gráfico se ninguém o reenviar; por isso a exposição vai na subida e de novo a
// cada 30 s, lida da memória e não do banco. O relógio vem de fora para o teste poder avançá-lo.
public sealed class SymbolExposureGaugeService(
    IDogStatsd orderMetricsClient, SymbolExposureMemory symbolExposureMemory, TimeProvider gaugeClock) : BackgroundService
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
        foreach (var currentSymbolExposure in symbolExposureMemory.CurrentSymbolExposures())
            orderMetricsClient.Gauge(
                OrderMetricNames.SymbolExposure, (double)currentSymbolExposure.Exposure,
                tags: OrderMetricTags.SymbolExposureTags(currentSymbolExposure.Symbol));
    }
}
