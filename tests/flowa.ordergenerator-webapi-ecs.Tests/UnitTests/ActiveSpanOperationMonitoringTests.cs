using System.Diagnostics;
using System.Diagnostics.Metrics;
using Flowa.OrderGenerator.Commons.Observability;
using Microsoft.Extensions.DependencyInjection;

namespace Flowa.OrderGenerator.Tests;

// Regression of the F4 self-review (CA-22, decision 16): each use case writes its result and duration as tags of the span that
// is already open, and its duration in a histogram with only the closed tags (use case and result); a rejected order is a tag,
// never an error status of the span; with no span open nothing breaks and the histogram still gets the measurement.
public sealed class ActiveSpanOperationMonitoringTests
{
    private const string OperationName = "orders.send-order";

    [Fact]
    public void Rejected_order_is_a_tag_of_the_open_span_and_a_histogram_measurement_with_only_closed_tags()
    {
        using var meterServices = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meterFactory = meterServices.GetRequiredService<IMeterFactory>();
        var durationMeasurements = new List<(double DurationInSeconds, KeyValuePair<string, object?>[] Tags)>();
        using var durationListener = ListenToOperationDurations(meterFactory, durationMeasurements);
        using var openSpanSource = new ActivitySource("Base.OrderGenerator.Tests.OpenSpan");
        using var openSpanListener = ListenToEverySpanOf(openSpanSource.Name);
        using var openSpan = openSpanSource.StartActivity("request")!;

        using (var monitoredOperation = new ActiveSpanOperationMonitoring(meterFactory).StartOperationMonitoring(OperationName))
            monitoredOperation.RecordOperationResult("rejected");

        Assert.Equal(OperationName, openSpan.GetTagItem("usecase"));
        Assert.Equal("rejected", openSpan.GetTagItem("result"));
        Assert.IsType<double>(openSpan.GetTagItem("duration_ms"));
        Assert.True((double)openSpan.GetTagItem("duration_ms")! >= 0);
        Assert.Equal(ActivityStatusCode.Unset, openSpan.Status);
        var durationMeasurement = Assert.Single(durationMeasurements);
        Assert.True(durationMeasurement.DurationInSeconds >= 0);
        Assert.Equal(
            [new KeyValuePair<string, object?>("usecase", OperationName), new KeyValuePair<string, object?>("result", "rejected")],
            durationMeasurement.Tags.OrderByDescending(durationTag => durationTag.Key == "usecase").ToArray());
    }

    [Fact]
    public void Without_an_open_span_the_operation_still_records_its_duration_with_the_default_result()
    {
        using var meterServices = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meterFactory = meterServices.GetRequiredService<IMeterFactory>();
        var durationMeasurements = new List<(double DurationInSeconds, KeyValuePair<string, object?>[] Tags)>();
        using var durationListener = ListenToOperationDurations(meterFactory, durationMeasurements);
        var spanBeforeTheOperation = Activity.Current;
        Activity.Current = null;

        try
        {
            using (new ActiveSpanOperationMonitoring(meterFactory).StartOperationMonitoring(OperationName))
            {
            }
        }
        finally
        {
            Activity.Current = spanBeforeTheOperation;
        }

        var durationMeasurement = Assert.Single(durationMeasurements);
        Assert.Contains(new KeyValuePair<string, object?>("result", "succeeded"), durationMeasurement.Tags);
        Assert.Equal(2, durationMeasurement.Tags.Length);
    }

    private static MeterListener ListenToOperationDurations(IMeterFactory meterFactory, List<(double, KeyValuePair<string, object?>[])> durationMeasurements)
    {
        var durationListener = new MeterListener
        {
            InstrumentPublished = (publishedInstrument, listener) =>
            {
                if (publishedInstrument.Meter.Scope == meterFactory && publishedInstrument.Name == ActiveSpanOperationMonitoring.OperationDurationMetricName)
                    listener.EnableMeasurementEvents(publishedInstrument);
            }
        };
        durationListener.SetMeasurementEventCallback<double>((_, durationInSeconds, durationTags, _) =>
            durationMeasurements.Add((durationInSeconds, durationTags.ToArray())));
        durationListener.Start();
        return durationListener;
    }

    private static ActivityListener ListenToEverySpanOf(string spanSourceName)
    {
        var spanListener = new ActivityListener
        {
            ShouldListenTo = spanSource => spanSource.Name == spanSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(spanListener);
        return spanListener;
    }
}
