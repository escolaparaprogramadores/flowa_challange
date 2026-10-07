using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Flowa.DatadogMetrics.Tests;

// One DogStatsD line: "name:value|type|#tag,tag". The tags become a sorted set
// so the comparison does not depend on the order in which the client writes them.
public sealed record DogStatsdMetricLine(string MetricName, string MetricValue, string MetricType, IReadOnlySet<string> MetricTags)
{
    public static DogStatsdMetricLine ParseDogStatsdLine(string dogStatsdLine)
    {
        var dogStatsdFields = dogStatsdLine.Split('|');
        var metricNameAndValue = dogStatsdFields[0].Split(':', 2);
        var metricTagsField = dogStatsdFields.Skip(2).FirstOrDefault(dogStatsdField => dogStatsdField.StartsWith('#'));
        var metricTags = metricTagsField is null ? [] : metricTagsField[1..].Split(',');
        return new DogStatsdMetricLine(metricNameAndValue[0], metricNameAndValue[1], dogStatsdFields[1], new SortedSet<string>(metricTags));
    }

    public bool Equals(DogStatsdMetricLine? otherMetricLine) =>
        otherMetricLine is not null
        && MetricName == otherMetricLine.MetricName
        && MetricValue == otherMetricLine.MetricValue
        && MetricType == otherMetricLine.MetricType
        && MetricTags.SetEquals(otherMetricLine.MetricTags);

    public override int GetHashCode() => HashCode.Combine(MetricName, MetricValue, MetricType);

    public override string ToString() => $"{MetricName}:{MetricValue}|{MetricType}|#{string.Join(',', MetricTags)}";
}

// Plays the Datadog agent: listens to UDP on IPv4 and IPv6, because "localhost" may resolve to either one.
public sealed class DogStatsdUdpListener : IDisposable
{
    private static readonly TimeSpan SentinelWaitLimit = TimeSpan.FromSeconds(15);
    private readonly UdpClient dogStatsdUdpClient;

    private static readonly TimeSpan BusyPortWaitLimit = TimeSpan.FromSeconds(60);

    public DogStatsdUdpListener(int listenerPort = 0)
    {
        dogStatsdUdpClient = new UdpClient(AddressFamily.InterNetworkV6);
        dogStatsdUdpClient.Client.DualMode = true;
        try
        {
            dogStatsdUdpClient.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, listenerPort));
        }
        catch
        {
            dogStatsdUdpClient.Dispose();
            throw;
        }
    }

    // The Accumulator test project also listens on the agent port and runs in parallel with this one:
    // the listener waits for the port to be free instead of failing the test.
    public static async Task<DogStatsdUdpListener> ListenOnTheAgentPortWhenFreeAsync(int agentPort)
    {
        using var busyPortWait = new CancellationTokenSource(BusyPortWaitLimit);
        while (true)
        {
            try
            {
                return new DogStatsdUdpListener(agentPort);
            }
            catch (SocketException portFailure) when (portFailure.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), busyPortWait.Token);
            }
        }
    }

    public int ListenerPort => ((IPEndPoint)dogStatsdUdpClient.Client.LocalEndPoint!).Port;

    public async Task<List<DogStatsdMetricLine>> ReadFlowaMetricsUntilAsync(string sentinelMetricName)
    {
        using var sentinelWait = new CancellationTokenSource(SentinelWaitLimit);
        var receivedFlowaMetrics = new List<DogStatsdMetricLine>();
        while (!receivedFlowaMetrics.Any(receivedFlowaMetric => receivedFlowaMetric.MetricName == sentinelMetricName))
        {
            var receivedDatagram = await dogStatsdUdpClient.ReceiveAsync(sentinelWait.Token);
            receivedFlowaMetrics.AddRange(Encoding.UTF8.GetString(receivedDatagram.Buffer)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(dogStatsdLine => dogStatsdLine.StartsWith("flowa."))
                .Select(DogStatsdMetricLine.ParseDogStatsdLine));
        }

        return receivedFlowaMetrics;
    }

    public void Dispose() => dogStatsdUdpClient.Dispose();
}
