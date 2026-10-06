using System.Diagnostics.Metrics;
using Base.OrderAccumulator.Commons.Logging;
using Base.OrderAccumulator.Commons.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Base.OrderAccumulator.Tests;

// The use cases and the gauge built by hand in the tests measure and log through the same Commons pieces the app registers.
public static class TestObservability
{
    public static IOperationMonitoring CreateOperationMonitoring() =>
        new ActiveSpanOperationMonitoring(new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>());

    public static IApplicationLogger<T> CreateDiscardingLogger<T>() => new ApplicationLogger<T>(NullLogger<T>.Instance);
}
