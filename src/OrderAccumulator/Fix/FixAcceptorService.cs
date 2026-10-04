using Flowa.Shared.Fix;
using QuickFix;
using QuickFix.Store;

namespace OrderAccumulator.Fix;

// Liga o acceptor FIX junto com o app e desliga na parada.
public sealed class FixAcceptorService(
    OrderFixApplication orderFixApplication, IConfiguration appConfiguration, ILoggerFactory orderAccumulatorLoggerFactory)
    : IHostedService, IDisposable
{
    private const string AcceptorSettingsFile = "acceptor.cfg";
    private const string Fix44DictionaryFile = "FIX44-flowa.xml";

    private ThreadedSocketAcceptor? fixAcceptor;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // O log FIX vai para o ILoggerFactory do app, que escreve no stdout (D-34), sem o valor da 5100.
        fixAcceptor = new ThreadedSocketAcceptor(
            orderFixApplication, new MemoryStoreFactory(), LoadFixAcceptorSessionSettings(appConfiguration),
            new LogDaSessaoFixSemTraceParent(orderAccumulatorLoggerFactory),
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
        var fixAcceptorPort = appConfiguration.GetValue<int?>("Fix:AcceptorPort")
            ?? throw new InvalidOperationException("Defina a porta do acceptor FIX em Fix__AcceptorPort.");
        var fixAcceptorBindHost = appConfiguration["Fix:AcceptorBindHost"];

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
    private sealed class LogDaSessaoFixSemTraceParent(ILoggerFactory logDoApp) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoriaDoLog) => new LogSemTraceParent(logDoApp.CreateLogger(categoriaDoLog));

        public void AddProvider(ILoggerProvider provedorDoLog) => logDoApp.AddProvider(provedorDoLog);

        public void Dispose() { }
    }

    private sealed class LogSemTraceParent(ILogger logDaCategoria) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState estadoDoEscopo) where TState : notnull => logDaCategoria.BeginScope(estadoDoEscopo);

        public bool IsEnabled(LogLevel nivelDoLog) => logDaCategoria.IsEnabled(nivelDoLog);

        public void Log<TState>(LogLevel nivelDoLog, EventId eventoDoLog, TState estadoDoLog, Exception? erroDoLog, Func<TState, Exception?, string> formatarLinha)
        {
            if (!logDaCategoria.IsEnabled(nivelDoLog))
                return;

            var linhaDoLog = formatarLinha(estadoDoLog, erroDoLog);
            var linhaSemTraceParent = RastroDaOrdemFix.OcultarTraceParentNoLog(linhaDoLog);
            if (ReferenceEquals(linhaDoLog, linhaSemTraceParent))
                logDaCategoria.Log(nivelDoLog, eventoDoLog, estadoDoLog, erroDoLog, formatarLinha);
            else
                logDaCategoria.Log(nivelDoLog, eventoDoLog, linhaSemTraceParent, erroDoLog, static (linha, _) => linha);
        }
    }
}
