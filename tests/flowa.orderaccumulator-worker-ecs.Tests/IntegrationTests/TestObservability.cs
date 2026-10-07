using System.Diagnostics.Metrics;
using Flowa.Commons.Logging;
using Flowa.Commons.Observability;
using Flowa.OrderAccumulator.Entrypoint.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Flowa.OrderAccumulator.Tests;

// The use cases built by hand in the tests measure and log through the same Commons pieces the app registers.
public static class TestObservability
{
    public static IOperationMonitoring CreateOperationMonitoring() =>
        new ActiveSpanOperationMonitoring(
            new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>(),
            OrderAccumulatorUseCaseDurationMetric.MeterName, OrderAccumulatorUseCaseDurationMetric.MetricName);

    public static IApplicationLogger<T> CreateDiscardingLogger<T>() => new ApplicationLogger<T>(NullLogger<T>.Instance);
}
