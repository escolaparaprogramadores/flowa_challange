using Flowa.Commons.Logging;
using Flowa.DatadogMetrics.Application.Exposures.UseCases;
using Flowa.DatadogMetrics.Application.Orders.Commands;
using Flowa.DatadogMetrics.Application.Orders.UseCases;

namespace Flowa.DatadogMetrics.Entrypoint.BackgroundService;

public sealed class DatadogMetricsBackgroundService : Microsoft.Extensions.Hosting.BackgroundService
{
    public static readonly TimeSpan DatadogMetricsSendInterval = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory metricsCycleScopeFactory;
    private readonly TimeProvider metricsClock;
    private readonly IApplicationLogger<DatadogMetricsBackgroundService> metricsLoopLogger;
    private long? lastCountedOrderId;

    public DatadogMetricsBackgroundService(
        IServiceScopeFactory metricsCycleScopeFactory,
        TimeProvider metricsClock,
        IApplicationLogger<DatadogMetricsBackgroundService> metricsLoopLogger)
    {
        this.metricsCycleScopeFactory = metricsCycleScopeFactory ?? throw new ArgumentNullException(nameof(metricsCycleScopeFactory));
        this.metricsClock = metricsClock ?? throw new ArgumentNullException(nameof(metricsClock));
        this.metricsLoopLogger = metricsLoopLogger ?? throw new ArgumentNullException(nameof(metricsLoopLogger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var datadogMetricsTimer = new PeriodicTimer(DatadogMetricsSendInterval, metricsClock);
        metricsLoopLogger.LogInformation("Datadog metrics loop started.", new { IntervalSeconds = DatadogMetricsSendInterval.TotalSeconds });
        do
        {
            await SendDatadogMetricsAsync(stoppingToken);
        }
        while (await datadogMetricsTimer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task SendDatadogMetricsAsync(CancellationToken stoppingToken)
    {
        await SendSymbolExposureGaugesAsync(stoppingToken);
        await SendAnsweredOrderCountsAsync(stoppingToken);
    }

    private async Task SendSymbolExposureGaugesAsync(CancellationToken stoppingToken)
    {
        await using var exposureGaugesScope = metricsCycleScopeFactory.CreateAsyncScope();
        var exposureGaugesMessage = await exposureGaugesScope.ServiceProvider.GetRequiredService<SendSymbolExposureGaugesUseCase>()
            .SendSymbolExposureGaugesAsync(stoppingToken);
        if (!exposureGaugesMessage.Success && !stoppingToken.IsCancellationRequested)
            metricsLoopLogger.LogError(exposureGaugesMessage.Failure!, "Symbol exposure gauges not sent. Trying again on the next cycle.",
                new { exposureGaugesMessage.ErrorCode });
    }

    private async Task SendAnsweredOrderCountsAsync(CancellationToken stoppingToken)
    {
        await using var orderCountsScope = metricsCycleScopeFactory.CreateAsyncScope();
        var orderCountsMessage = await orderCountsScope.ServiceProvider.GetRequiredService<SendAnsweredOrderCountsUseCase>()
            .SendAnsweredOrderCountsAsync(new SendAnsweredOrderCountsCommand(lastCountedOrderId), stoppingToken);
        if (orderCountsMessage.Success)
            lastCountedOrderId = orderCountsMessage.Data!.LastCountedOrderId;
        else if (!stoppingToken.IsCancellationRequested)
            metricsLoopLogger.LogError(orderCountsMessage.Failure!, "Answered order counts not sent. Trying again on the next cycle.",
                new { orderCountsMessage.ErrorCode });
    }
}
