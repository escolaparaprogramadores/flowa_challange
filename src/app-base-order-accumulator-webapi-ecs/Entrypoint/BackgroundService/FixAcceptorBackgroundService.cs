using Base.OrderAccumulator.Entrypoint.Fix;
using Base.OrderAccumulator.Infrastructure.Orders.Options;
using Base.OrderAccumulator.Infrastructure.Fix;
using QuickFix.Store;
using QuickFix;

namespace Base.OrderAccumulator.Entrypoint.BackgroundService;

public sealed class FixAcceptorBackgroundService : IHostedService, IDisposable
{
    private const string AcceptorSettingsFile = "acceptor.cfg";
    private const string Fix44DictionaryFile = "FIX44-flowa.xml";

    private readonly NewOrderSingleConsumer newOrderSingleConsumer;
    private readonly IConfiguration appConfiguration;
    private readonly FixSessionLogFactory fixSessionLogFactory;
    private ThreadedSocketAcceptor? fixAcceptor;

    public FixAcceptorBackgroundService(
        NewOrderSingleConsumer newOrderSingleConsumer, IConfiguration appConfiguration, FixSessionLogFactory fixSessionLogFactory)
    {
        this.newOrderSingleConsumer = newOrderSingleConsumer ?? throw new ArgumentNullException(nameof(newOrderSingleConsumer));
        this.appConfiguration = appConfiguration ?? throw new ArgumentNullException(nameof(appConfiguration));
        this.fixSessionLogFactory = fixSessionLogFactory ?? throw new ArgumentNullException(nameof(fixSessionLogFactory));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
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

    private void ShutDownFixAcceptor()
    {
        var runningFixAcceptor = Interlocked.Exchange(ref fixAcceptor, null);
        if (runningFixAcceptor is null)
            return;

        runningFixAcceptor.Stop();
        runningFixAcceptor.Dispose();
    }

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
