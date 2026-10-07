using Flowa.Commons.DependencyInjection;
using Flowa.DatadogMetrics.Application.Exposures.UseCases;
using Flowa.DatadogMetrics.Application.Orders.UseCases;
using Flowa.DatadogMetrics.Entrypoint.BackgroundService;
using Flowa.DatadogMetrics.Entrypoint.Observability;
using Flowa.DatadogMetrics.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Flowa.DatadogMetrics.Entrypoint.DependencyInjection;

public static class DatadogMetricsWorkerExtensions
{
    extension(IServiceCollection datadogMetricsServices)
    {
        public IServiceCollection AddDatadogMetricsWorker(string flowaConnectionString, IConfiguration datadogMetricsConfiguration)
        {
            datadogMetricsServices.AddApplicationLogger();
            datadogMetricsServices.AddOperationMonitoring(DatadogMetricsUseCaseDurationMetric.MeterName, DatadogMetricsUseCaseDurationMetric.MetricName);
            datadogMetricsServices.AddDatadogMetricsInfrastructure(flowaConnectionString, datadogMetricsConfiguration);
            datadogMetricsServices.TryAddSingleton(TimeProvider.System);
            datadogMetricsServices.AddScoped<SendSymbolExposureGaugesUseCase>();
            datadogMetricsServices.AddScoped<SendAnsweredOrderCountsUseCase>();
            datadogMetricsServices.AddHostedService<DatadogMetricsBackgroundService>();
            return datadogMetricsServices;
        }
    }
}
