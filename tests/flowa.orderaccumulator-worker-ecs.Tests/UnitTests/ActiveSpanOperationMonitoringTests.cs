using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Flowa.Commons.Observability;
using Flowa.OrderAccumulator.Entrypoint.Observability;
using Microsoft.Extensions.DependencyInjection;

namespace Flowa.OrderAccumulator.Tests;

// CA-22: the use case measurement opens no span of its own; it tags the span already open and records the duration
// in a histogram with only the closed tags usecase and result. A rejection is a tag, never an error status.
public sealed class ActiveSpanOperationMonitoringTests : IDisposable
{
    private const string OpenSpanTestSourceName = "Base.OrderAccumulator.Tests.OpenSpan";

    private static readonly ActivitySource OpenSpanTestSource = new(OpenSpanTestSourceName);

    private readonly IMeterFactory meterFactory = new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>();
    private readonly ConcurrentQueue<RecordedDuration> recordedDurations = new();
    private readonly MeterListener durationListener = new();
    private readonly ActivityListener spanListener;

    public ActiveSpanOperationMonitoringTests()
    {
        durationListener.InstrumentPublished = (publishedInstrument, listener) =>
        {
            if (publishedInstrument.Meter.Scope == meterFactory && publishedInstrument.Name == OrderAccumulatorUseCaseDurationMetric.MetricName)
                listener.EnableMeasurementEvents(publishedInstrument);
        };
        durationListener.SetMeasurementEventCallback<double>((publishedInstrument, durationSeconds, durationTags, _) =>
            recordedDurations.Enqueue(new RecordedDuration(
                publishedInstrument.Meter.Name, publishedInstrument.Unit, durationSeconds,
                durationTags.ToArray().ToDictionary(durationTag => durationTag.Key, durationTag => durationTag.Value?.ToString()))));
        durationListener.Start();

        spanListener = new ActivityListener
        {
            ShouldListenTo = traceSource => traceSource.Name == OpenSpanTestSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(spanListener);
    }

    [Fact]
    public void Rejected_result_is_a_tag_on_the_open_span_and_a_histogram_measurement_without_error_status()
    {
        var operationMonitoring = new ActiveSpanOperationMonitoring(meterFactory, OrderAccumulatorUseCaseDurationMetric.MeterName, OrderAccumulatorUseCaseDurationMetric.MetricName);
        using var openSpan = OpenSpanTestSource.StartActivity("fix.recebimento_da_ordem")!;

        using (var decisionMonitoring = operationMonitoring.StartOperationMonitoring("orders.decide-incoming-order"))
            decisionMonitoring.RecordOperationResult("rejected");

        Assert.Equal("orders.decide-incoming-order", openSpan.GetTagItem(ActiveSpanOperationMonitoring.OperationNameTag));
        Assert.Equal("rejected", openSpan.GetTagItem(ActiveSpanOperationMonitoring.OperationResultTag));
        Assert.IsType<double>(openSpan.GetTagItem(ActiveSpanOperationMonitoring.OperationDurationTag));
        Assert.Equal(ActivityStatusCode.Unset, openSpan.Status);
        var recordedDuration = Assert.Single(recordedDurations);
        Assert.Equal(("Base.OrderAccumulator", "s"), (recordedDuration.MeterName, recordedDuration.Unit));
        Assert.Equal(
            new Dictionary<string, string?> { ["usecase"] = "orders.decide-incoming-order", ["result"] = "rejected" },
            recordedDuration.DurationTags);
        Assert.InRange(recordedDuration.DurationSeconds, 0d, 5d);
        Assert.Equal(recordedDuration.DurationSeconds * 1000d, (double)openSpan.GetTagItem(ActiveSpanOperationMonitoring.OperationDurationTag)!, 6);
    }

    [Fact]
    public void Without_an_open_span_the_operation_only_records_the_histogram_as_succeeded()
    {
        var operationMonitoring = new ActiveSpanOperationMonitoring(meterFactory, OrderAccumulatorUseCaseDurationMetric.MeterName, OrderAccumulatorUseCaseDurationMetric.MetricName);
        Assert.Null(Activity.Current);

        using (operationMonitoring.StartOperationMonitoring("orders.list-orders"))
        {
        }

        Assert.Null(Activity.Current);
        var recordedDuration = Assert.Single(recordedDurations);
        Assert.Equal(
            new Dictionary<string, string?> { ["usecase"] = "orders.list-orders", ["result"] = "succeeded" },
            recordedDuration.DurationTags);
    }

    public void Dispose()
    {
        durationListener.Dispose();
        spanListener.Dispose();
    }

    private sealed record RecordedDuration(string MeterName, string? Unit, double DurationSeconds, Dictionary<string, string?> DurationTags);
}
