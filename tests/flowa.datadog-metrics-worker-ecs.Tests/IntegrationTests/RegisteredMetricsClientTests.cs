using Flowa.Commons.Logging;
using Flowa.Commons.Observability;
using Flowa.DatadogMetrics.Application.Exposures.Interfaces;
using Flowa.DatadogMetrics.Application.Exposures.UseCases;
using Flowa.DatadogMetrics.Application.Orders.UseCases;
using Flowa.DatadogMetrics.Domain.Exposures.ValueObjects;
using Flowa.DatadogMetrics.Entrypoint.BackgroundService;
using Flowa.DatadogMetrics.Entrypoint.DependencyInjection;
using Flowa.DatadogMetrics.Infrastructure.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Flowa.DatadogMetrics.Tests;

// The other tests swap the metrics client, the logger and the clock; these keep what Program registers
// (AddDatadogMetricsWorker) and listen where the Datadog agent listens in the ECS task.
public sealed class RegisteredMetricsClientTests
{
    [Fact]
    public async Task Worker_registers_the_dogstatsd_client_that_sends_to_the_agent_on_localhost_8125_with_the_dd_tags()
    {
        // Arrange
        var uniqueVersionTag = "sha-" + Guid.NewGuid().ToString("N");
        var uniqueSentinelMetricName = "flowa.test.sentinel." + Guid.NewGuid().ToString("N");
        using var agentOnTheDatadogPort = await DogStatsdUdpListener.ListenOnTheAgentPortWhenFreeAsync(
            DatadogMetricsInfrastructureExtensions.DatadogAgentDogStatsdPort);
        var unifiedServiceConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DD_ENV"] = "dev",
                ["DD_SERVICE"] = "datadog-metrics",
                ["DD_VERSION"] = uniqueVersionTag
            })
            .Build();
        var workerServiceCollection = new ServiceCollection();
        workerServiceCollection.AddMetrics();
        workerServiceCollection.AddDatadogMetricsWorker("Host=127.0.0.1;Database=flowa", unifiedServiceConfiguration);
        await using var workerServices = workerServiceCollection.BuildServiceProvider();
        var registeredMetricsClient = Assert.IsType<DogStatsdMetricsClient>(workerServices.GetRequiredService<IMetricsClient>());

        // Act
        workerServices.GetRequiredService<IExposureMetricsPort>().SendSymbolExposureGauge(new SymbolExposure("PETR4", 42m));
        registeredMetricsClient.FlushPendingMetrics();
        registeredMetricsClient.IncrementCounter(uniqueSentinelMetricName, []);
        registeredMetricsClient.FlushPendingMetrics();
        var receivedOnTheAgentPort = await agentOnTheDatadogPort.ReadFlowaMetricsUntilAsync(uniqueSentinelMetricName);

        // Assert
        var exposureGauge = Assert.Single(receivedOnTheAgentPort, receivedMetric =>
            receivedMetric.MetricName == "flowa.exposicao" && receivedMetric.MetricTags.Contains($"version:{uniqueVersionTag}"));
        Assert.Equal(
            new DogStatsdMetricLine("flowa.exposicao", "42", "g",
                new SortedSet<string> { "env:dev", "service:datadog-metrics", $"version:{uniqueVersionTag}", "symbol:PETR4" }),
            exposureGauge);
    }

    [Fact]
    public async Task Worker_registration_runs_one_metrics_loop_on_the_system_clock_with_both_use_cases()
    {
        // Arrange
        var workerServiceCollection = new ServiceCollection();
        workerServiceCollection.AddMetrics();
        workerServiceCollection.AddLogging();

        // Act
        workerServiceCollection.AddDatadogMetricsWorker("Host=127.0.0.1;Database=flowa", new ConfigurationBuilder().Build());
        await using var workerServices = workerServiceCollection.BuildServiceProvider();
        await using var metricsCycleScope = workerServices.CreateAsyncScope();

        // Assert
        Assert.IsType<DatadogMetricsBackgroundService>(Assert.Single(workerServices.GetServices<IHostedService>()));
        Assert.Same(TimeProvider.System, workerServices.GetRequiredService<TimeProvider>());
        Assert.IsType<DogStatsdMetricsClient>(workerServices.GetRequiredService<IMetricsClient>());
        Assert.IsType<ApplicationLogger<DatadogMetricsBackgroundService>>(workerServices.GetRequiredService<IApplicationLogger<DatadogMetricsBackgroundService>>());
        Assert.IsType<SendSymbolExposureGaugesUseCase>(metricsCycleScope.ServiceProvider.GetRequiredService<SendSymbolExposureGaugesUseCase>());
        Assert.IsType<SendAnsweredOrderCountsUseCase>(metricsCycleScope.ServiceProvider.GetRequiredService<SendAnsweredOrderCountsUseCase>());
    }
}
