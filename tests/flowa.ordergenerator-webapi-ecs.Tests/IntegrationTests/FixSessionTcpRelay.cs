using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Flowa.OrderGenerator.Tests;

// Sits between the OrderGenerator and the test acceptor. Cutting it resets both sockets at once, like the
// OrderAccumulator task dying; the test acceptor alone only closes its socket on its next 1 s read poll.
public sealed class FixSessionTcpRelay : IDisposable
{
    private readonly TcpListener _relayListener = new(IPAddress.Loopback, 0);
    private readonly ConcurrentQueue<TcpClient> _relayedConnections = new();
    private readonly CancellationTokenSource _fixSessionCut = new();
    private readonly int _acceptorPort;

    public FixSessionTcpRelay(int acceptorPort)
    {
        _acceptorPort = acceptorPort;
        _relayListener.Start();
        RelayPort = ((IPEndPoint)_relayListener.LocalEndpoint).Port;
        _ = RelayOrderGeneratorConnectionsAsync();
    }

    public int RelayPort { get; }

    public void CutFixSession()
    {
        if (_fixSessionCut.IsCancellationRequested)
            return;

        _fixSessionCut.Cancel();
        _relayListener.Stop();
        foreach (var relayedConnection in _relayedConnections)
        {
            relayedConnection.Client.LingerState = new LingerOption(true, 0);
            relayedConnection.Close();
        }
    }

    public void Dispose() => CutFixSession();

    private async Task RelayOrderGeneratorConnectionsAsync()
    {
        try
        {
            while (true)
            {
                var orderGeneratorConnection = await _relayListener.AcceptTcpClientAsync(_fixSessionCut.Token);
                var acceptorConnection = new TcpClient();
                await acceptorConnection.ConnectAsync(IPAddress.Loopback, _acceptorPort, _fixSessionCut.Token);
                _relayedConnections.Enqueue(orderGeneratorConnection);
                _relayedConnections.Enqueue(acceptorConnection);
                _ = CopyFixBytesUntilCutAsync(orderGeneratorConnection, acceptorConnection);
                _ = CopyFixBytesUntilCutAsync(acceptorConnection, orderGeneratorConnection);
            }
        }
        catch (Exception) when (_fixSessionCut.IsCancellationRequested)
        {
        }
    }

    private async Task CopyFixBytesUntilCutAsync(TcpClient sourceConnection, TcpClient targetConnection)
    {
        try
        {
            await sourceConnection.GetStream().CopyToAsync(targetConnection.GetStream(), _fixSessionCut.Token);
            targetConnection.Client.Shutdown(SocketShutdown.Send);
        }
        catch (Exception) when (_fixSessionCut.IsCancellationRequested)
        {
        }
    }
}
