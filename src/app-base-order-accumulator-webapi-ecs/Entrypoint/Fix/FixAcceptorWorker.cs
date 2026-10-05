using Base.OrderAccumulator.Commons;
using Base.OrderAccumulator.Infrastructure.Fix;
using QuickFix.Store;
using QuickFix;

namespace Base.OrderAccumulator.Entrypoint.Fix;

// Liga o acceptor FIX junto com o app e desliga na parada.
public sealed class FixAcceptorWorker(
    NewOrderSingleConsumer newOrderSingleConsumer, IConfiguration appConfiguration, ILoggerFactory orderAccumulatorLoggerFactory)
    : IHostedService, IDisposable
{
    private const string AcceptorSettingsFile = "acceptor.cfg";
    private const string Fix44DictionaryFile = "FIX44-flowa.xml";

    private ThreadedSocketAcceptor? fixAcceptor;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // O log FIX vai para o ILoggerFactory do app, que escreve no stdout (D-34), sem o valor da 5100.
        fixAcceptor = new ThreadedSocketAcceptor(
            newOrderSingleConsumer, new MemoryStoreFactory(), LoadFixAcceptorSessionSettings(appConfiguration),
            new TraceParentHidingLoggerFactory(orderAccumulatorLoggerFactory),
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

    // Parar e descartar chegam em qualquer ordem (até juntos); só quem tira o acceptor da referência o desliga.
    private void ShutDownFixAcceptor()
    {
        var runningFixAcceptor = Interlocked.Exchange(ref fixAcceptor, null);
        if (runningFixAcceptor is null)
            return;

        runningFixAcceptor.Stop();
        runningFixAcceptor.Dispose();
    }

    // A sessão vem do acceptor.cfg. A porta vem da configuração (Fix__AcceptorPort) e o dicionário
    // é procurado ao lado do executável, para não depender da pasta de onde o app foi iniciado.
    // Fix__AcceptorBindHost é opcional: sem ele o acceptor escuta em todas as interfaces, como o
    // compose precisa; os testes usam 127.0.0.1 para não abrir a porta para a rede.
    public static SessionSettings LoadFixAcceptorSessionSettings(IConfiguration appConfiguration)
    {
        var fixAcceptorPort = appConfiguration.GetValue<int?>(OrderAccumulatorConfigurationKeys.FixAcceptorPort)
            ?? throw new InvalidOperationException("Defina a porta do acceptor FIX em Fix__AcceptorPort.");
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

    // O log de sessão do QuickFIX escreve a mensagem FIX crua; o valor da 5100 (trace id) fica de fora.
    // A fábrica é a do app (injeção de dependência): quem a descarta é o host, não o acceptor.
    private sealed class TraceParentHidingLoggerFactory(ILoggerFactory appLoggerFactory) : ILoggerFactory
    {
        public ILogger CreateLogger(string logCategoryName) => new TraceParentHidingLogger(appLoggerFactory.CreateLogger(logCategoryName));

        public void AddProvider(ILoggerProvider loggerProvider) => appLoggerFactory.AddProvider(loggerProvider);

        public void Dispose() { }
    }

    private sealed class TraceParentHidingLogger(ILogger categoryLogger) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState scopeState) where TState : notnull => categoryLogger.BeginScope(scopeState);

        public bool IsEnabled(LogLevel logLevel) => categoryLogger.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId logEventId, TState logState, Exception? logException, Func<TState, Exception?, string> formatLogLine)
        {
            if (!categoryLogger.IsEnabled(logLevel))
                return;

            var logLine = formatLogLine(logState, logException);
            var logLineWithoutTraceParent = FixOrderTraceProvider.HideTraceParentInLog(logLine);
            if (ReferenceEquals(logLine, logLineWithoutTraceParent))
                categoryLogger.Log(logLevel, logEventId, logState, logException, formatLogLine);
            else
                categoryLogger.Log(logLevel, logEventId, logLineWithoutTraceParent, logException, static (hiddenLogLine, _) => hiddenLogLine);
        }
    }
}
