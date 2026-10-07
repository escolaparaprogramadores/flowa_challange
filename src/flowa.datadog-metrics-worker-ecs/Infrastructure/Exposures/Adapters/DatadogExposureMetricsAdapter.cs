using Flowa.Commons.Observability;
using Flowa.DatadogMetrics.Application.Exposures.Interfaces;
using Flowa.DatadogMetrics.Domain.Exposures.ValueObjects;

namespace Flowa.DatadogMetrics.Infrastructure.Exposures.Adapters;

internal sealed class DatadogExposureMetricsAdapter : IExposureMetricsPort
{
    public const string SymbolExposureMetricName = "flowa.exposicao";

    private readonly IMetricsClient datadogMetricsClient;

    public DatadogExposureMetricsAdapter(IMetricsClient datadogMetricsClient)
    {
        this.datadogMetricsClient = datadogMetricsClient ?? throw new ArgumentNullException(nameof(datadogMetricsClient));
    }

    public void SendSymbolExposureGauge(SymbolExposure symbolExposure) =>
        datadogMetricsClient.RecordGauge(SymbolExposureMetricName, (double)symbolExposure.Exposure, [$"symbol:{symbolExposure.Symbol}"]);
}
