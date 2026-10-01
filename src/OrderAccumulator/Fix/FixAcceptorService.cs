using QuickFix;
using QuickFix.Store;

namespace OrderAccumulator.Fix;

// Liga o acceptor FIX junto com o app e desliga na parada.
public sealed class FixAcceptorService(
    OrderFixApplication application, IConfiguration configuration, ILoggerFactory loggerFactory)
    : IHostedService, IDisposable
{
    private const string SettingsFile = "acceptor.cfg";
    private const string DictionaryFile = "FIX44.xml";

    private ThreadedSocketAcceptor? acceptor;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // O log FIX vai para o ILoggerFactory do app, que escreve no stdout (D-34).
        acceptor = new ThreadedSocketAcceptor(
            application, new MemoryStoreFactory(), LoadSessionSettings(configuration), loggerFactory,
            new DefaultMessageFactory([typeof(QuickFix.FIX44.NewOrderSingle).Assembly], string.Empty));
        acceptor.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        acceptor?.Stop();
        return Task.CompletedTask;
    }

    public void Dispose() => acceptor?.Dispose();

    // A sessão vem do acceptor.cfg. A porta vem da configuração (Fix__AcceptorPort) e o dicionário
    // é procurado ao lado do executável, para não depender da pasta de onde o app foi iniciado.
    // Fix__AcceptorBindHost é opcional: sem ele o acceptor escuta em todas as interfaces, como o
    // compose precisa; os testes usam 127.0.0.1 para não abrir a porta para a rede.
    public static SessionSettings LoadSessionSettings(IConfiguration configuration)
    {
        var port = configuration.GetValue<int?>("Fix:AcceptorPort")
            ?? throw new InvalidOperationException("Defina a porta do acceptor FIX em Fix__AcceptorPort.");
        var bindHost = configuration["Fix:AcceptorBindHost"];

        var acceptorSettings = new SessionSettings(Path.Combine(AppContext.BaseDirectory, SettingsFile));
        foreach (var sessionId in acceptorSettings.GetSessions())
        {
            var sessionSettings = acceptorSettings.Get(sessionId);
            sessionSettings.SetLong("SocketAcceptPort", port);
            sessionSettings.SetString("DataDictionary", Path.Combine(AppContext.BaseDirectory, DictionaryFile));
            if (!string.IsNullOrWhiteSpace(bindHost))
                sessionSettings.SetString("SocketAcceptHost", bindHost);
        }

        return acceptorSettings;
    }
}
