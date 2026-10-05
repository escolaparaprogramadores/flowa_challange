using Base.OrderAccumulator.Commons;
using Base.OrderAccumulator.Infrastructure.Fix;
using QuickFix.Store;
using QuickFix;

namespace Base.OrderAccumulator.Entrypoint.Fix;

// Starts the FIX acceptor together with the app and shuts it down on stop.
public sealed class FixAcceptorWorker(
    NewOrderSingleConsumer newOrderSingleConsumer, IConfiguration appConfiguration, FixSessionLogFactory fixSessionLogFactory)
    : IHostedService, IDisposable
{
    private const string AcceptorSettingsFile = "acceptor.cfg";
    private const string Fix44DictionaryFile = "FIX44-flowa.xml";

    private ThreadedSocketAcceptor? fixAcceptor;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // The FIX log goes through the application logger to stdout, one JSON line per message (D-34).
        fixAcceptor = new ThreadedSocketAcceptor(
            newOrderSingleConsumer, new MemoryStoreFactory(), LoadFixAcceptorSessionSettings(appConfiguration),
            fixSessionLogFactory,
            new DefaultMessageFactory([typeof(QuickFix.FIX44.NewOrderSingle).Assembly], string.Empty));
        fixAcceptor.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        ShutDownFixAcceptor();
        return Task.CompletedTask;
    }

    public void Dispose() => ShutDownFixAcceptor();

    // Stop and dispose arrive in any order (even together); only the one that takes the acceptor out of the reference shuts it down.
    private void ShutDownFixAcceptor()
    {
        var runningFixAcceptor = Interlocked.Exchange(ref fixAcceptor, null);
        if (runningFixAcceptor is null)
            return;

        runningFixAcceptor.Stop();
        runningFixAcceptor.Dispose();
    }

    // The session comes from acceptor.cfg. The port comes from configuration (Fix__AcceptorPort) and the dictionary
    // is looked up next to the executable, so it does not depend on the folder the app was started from.
    // Fix__AcceptorBindHost is optional: without it the acceptor listens on all interfaces, as the
    // compose needs; the tests use 127.0.0.1 so the port is not opened to the network.
    public static SessionSettings LoadFixAcceptorSessionSettings(IConfiguration appConfiguration)
    {
        var fixAcceptorPort = appConfiguration.GetValue<int?>(OrderAccumulatorConfigurationKeys.FixAcceptorPort)
            ?? throw new InvalidOperationException("Set the FIX acceptor port in Fix__AcceptorPort.");
        var fixAcceptorBindHost = appConfiguration[OrderAccumulatorConfigurationKeys.FixAcceptorBindHost];

        var loadedFixAcceptorSettings = new SessionSettings(Path.Combine(AppContext.BaseDirectory, AcceptorSettingsFile));
        foreach (var fixSessionId in loadedFixAcceptorSettings.GetSessions())
        {
            var fixSessionSettings = loadedFixAcceptorSettings.Get(fixSessionId);
            fixSessionSettings.SetLong("SocketAcceptPort", fixAcceptorPort);
            fixSessionSettings.SetString("DataDictionary", Path.Combine(AppContext.BaseDirectory, Fix44DictionaryFile));
            if (!string.IsNullOrWhiteSpace(fixAcceptorBindHost))
                fixSessionSettings.SetString("SocketAcceptHost", fixAcceptorBindHost);
        }

        return loadedFixAcceptorSettings;
    }
}
