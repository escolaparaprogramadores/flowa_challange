using Flowa.DatadogMetrics.Domain.Exposures.ValueObjects;

namespace Flowa.DatadogMetrics.Application.Exposures.Interfaces;

public interface IExposureMetricsPort
{
    void SendSymbolExposureGauge(SymbolExposure symbolExposure);
}
