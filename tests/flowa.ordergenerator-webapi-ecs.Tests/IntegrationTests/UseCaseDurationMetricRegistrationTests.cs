using System.Diagnostics.Metrics;
using Flowa.Commons.Observability;
using Microsoft.Extensions.DependencyInjection;

namespace Flowa.OrderGenerator.Tests;

// The Commons is shared by the apps, so the meter and the duration metric come from the app: the Program of the
// OrderGenerator has to register the use case measurement with its own names, the ones the dashboard reads.
public sealed class UseCaseDurationMetricRegistrationTests
{
    [Fact]
    public async Task Program_registers_the_use_case_measurement_with_the_order_generator_meter_and_metric()
    {
        await using var orderGeneratorFactory = OrderGeneratorTestHost.CreateOrderGeneratorFactory(OrderGeneratorTestHost.FindFreeTcpPort());
        var orderGeneratorMeterFactory = orderGeneratorFactory.Services.GetRequiredService<IMeterFactory>();
        var recordedUseCaseDurations = new List<(string MeterName, string MetricName, string? MetricUnit)>();
        using var useCaseDurationListener = new MeterListener
        {
            InstrumentPublished = (publishedInstrument, listener) =>
            {
                if (publishedInstrument.Meter.Scope == orderGeneratorMeterFactory)
                    listener.EnableMeasurementEvents(publishedInstrument);
            }
        };
        useCaseDurationListener.SetMeasurementEventCallback<double>((publishedInstrument, _, _, _) =>
            recordedUseCaseDurations.Add((publishedInstrument.Meter.Name, publishedInstrument.Name, publishedInstrument.Unit)));
        useCaseDurationListener.Start();

        using (orderGeneratorFactory.Services.GetRequiredService<IOperationMonitoring>().StartOperationMonitoring("orders.send-order"))
        {
        }

        Assert.Equal(("Base.OrderGenerator", "ordergenerator.usecase.duration", "s"), Assert.Single(recordedUseCaseDurations));
    }
}
