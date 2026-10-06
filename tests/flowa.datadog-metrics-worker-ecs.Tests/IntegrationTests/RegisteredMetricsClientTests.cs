using Flowa.Commons.Observability;
using Flowa.DatadogMetrics.Application.Exposures.Interfaces;
using Flowa.DatadogMetrics.Domain.Exposures.ValueObjects;
using Flowa.DatadogMetrics.Infrastructure.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Flowa.DatadogMetrics.Tests;

// The other tests swap the metrics client for one pointed at a free port; this one keeps the client the worker
// registers and listens where the Datadog agent listens in the ECS task.
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
        workerServiceCollection.AddDatadogMetricsInfrastructure("Host=127.0.0.1;Database=flowa", unifiedServiceConfiguration);
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
}
