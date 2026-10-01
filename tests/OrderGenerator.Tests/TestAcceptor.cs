using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using QuickFix;
using QuickFix.Fields;
using QuickFix.Logger;
using QuickFix.Store;

// Os testes sobem sessões FIX com o mesmo SessionID; o QuickFIX guarda as sessões num registro
// estático, então duas classes rodando juntas brigariam pelo mesmo nome.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace OrderGenerator.Tests;

// Faz o papel do OrderAccumulator só no que o OrderGenerator enxerga: aceita a sessão FIX,
// guarda as NewOrderSingle recebidas e responde com o ExecutionReport que o teste escolher.
public sealed class TestAcceptor : IApplication, IDisposable
{
    private ThreadedSocketAcceptor? _acceptor;
    private TaskCompletionSource _acceptorLogon = NewSignal();
    private int _executionReportNumber;

    public TestAcceptor(int port) => Port = port;

    public int Port { get; }

    public ConcurrentQueue<Message> ReceivedOrders { get; } = new();

    // Cada ExecutionReport mandado de volta, pelo ClOrdID que ele carrega.
    public ConcurrentDictionary<string, Message> SentExecutionReports { get; } = new();

    // Monta a resposta para a ordem recebida; null deixa a ordem sem resposta (acceptor mudo).
    public Func<Message, Message?> ExecutionReportResponder { get; set; } = receivedOrder => null;

    // Quanto esperar antes de mandar a resposta, por ordem.
    public Func<Message, TimeSpan> ExecutionReportDelay { get; set; } = receivedOrder => TimeSpan.Zero;

    // Relatório extra mandado antes da resposta, com ClOrdID que não é o da ordem.
    public Func<Message, Message?> StrayExecutionReport { get; set; } = receivedOrder => null;

    public void ResetToAcceptEveryOrder()
    {
        ExecutionReportResponder = BuildAcceptedReport;
        ExecutionReportDelay = receivedOrder => TimeSpan.Zero;
        StrayExecutionReport = receivedOrder => null;
        ReceivedOrders.Clear();
        SentExecutionReports.Clear();
    }

    public void Start()
    {
        _acceptorLogon = NewSignal();
        var acceptorSettings = new SessionSettings(new StringReader($"""
            [DEFAULT]
            ConnectionType=acceptor
            SocketAcceptHost=127.0.0.1
            SocketAcceptPort={Port}
            StartTime=00:00:00
            EndTime=00:00:00
            UseDataDictionary=Y
            DataDictionary={Path.Combine(AppContext.BaseDirectory, "FIX44.xml")}
            ResetOnLogon=Y
            ResetOnLogout=Y
            ResetOnDisconnect=Y

            [SESSION]
            BeginString=FIX.4.4
            SenderCompID=ORDERACCUMULATOR
            TargetCompID=ORDERGENERATOR
            """));
        _acceptor = new ThreadedSocketAcceptor(this, new MemoryStoreFactory(), acceptorSettings, new NullLogFactory(), new DefaultMessageFactory());
        _acceptor.Start();
    }

    public void Stop()
    {
        _acceptor?.Stop(true);
        _acceptor?.Dispose();
        _acceptor = null;
    }

    // Espera as duas pontas: o acceptor recebeu o Logon e o initiator já recebeu a resposta dele.
    public async Task WaitForLogonAsync()
    {
        await _acceptorLogon.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var initiatorSessionId = new SessionID("FIX.4.4", "ORDERGENERATOR", "ORDERACCUMULATOR");
        var logonClock = Stopwatch.StartNew();
        while (Session.LookupSession(initiatorSessionId)?.IsLoggedOn != true)
        {
            if (logonClock.Elapsed > TimeSpan.FromSeconds(15))
                throw new TimeoutException("O initiator não logou.");
            await Task.Delay(50);
        }
    }

    public Message BuildAcceptedReport(Message receivedOrder) =>
        BuildExecutionReport(receivedOrder, ExecType.NEW, OrdStatus.NEW, receivedOrder.GetDecimal(Tags.OrderQty));

    public Message BuildRejectedReport(Message receivedOrder, string rejectionText)
    {
        var rejectedReport = BuildExecutionReport(receivedOrder, ExecType.REJECTED, OrdStatus.REJECTED, 0);
        rejectedReport.SetField(new Text(rejectionText));
        return rejectedReport;
    }

    public Message BuildExecutionReport(Message receivedOrder, char execType, char ordStatus, decimal leavesQty, string? clOrdId = null)
    {
        var reportNumber = Interlocked.Increment(ref _executionReportNumber);
        var executionReport = new QuickFix.FIX44.ExecutionReport(
            new OrderID($"ORD-{reportNumber}"), new ExecID($"EXE-{reportNumber}"),
            new ExecType(execType), new OrdStatus(ordStatus),
            new Symbol(receivedOrder.GetString(Tags.Symbol)), new Side(receivedOrder.GetChar(Tags.Side)),
            new LeavesQty(leavesQty), new CumQty(0), new AvgPx(0));
        executionReport.Set(new ClOrdID(clOrdId ?? receivedOrder.GetString(Tags.ClOrdID)));
        return executionReport;
    }

    public void FromApp(Message message, SessionID sessionID)
    {
        if (message.Header.GetString(Tags.MsgType) != MsgType.NEW_ORDER_D)
            return;

        ReceivedOrders.Enqueue(message);
        var strayExecutionReport = StrayExecutionReport(message);
        if (strayExecutionReport is not null)
            SendExecutionReport(strayExecutionReport, sessionID);

        var executionReport = ExecutionReportResponder(message);
        if (executionReport is null)
            return;

        var executionReportDelay = ExecutionReportDelay(message);
        if (executionReportDelay == TimeSpan.Zero)
            SendExecutionReport(executionReport, sessionID);
        else
            // Fora da thread da sessão, para as outras ordens seguirem chegando enquanto esta espera.
            _ = Task.Delay(executionReportDelay).ContinueWith(_ => SendExecutionReport(executionReport, sessionID), TaskScheduler.Default);
    }

    private void SendExecutionReport(Message executionReport, SessionID sessionID)
    {
        SentExecutionReports[executionReport.GetString(Tags.ClOrdID)] = executionReport;
        Session.SendToTarget(executionReport, sessionID);
    }

    public void OnLogon(SessionID sessionID) => _acceptorLogon.TrySetResult();

    public void OnCreate(SessionID sessionID) { }

    public void OnLogout(SessionID sessionID) { }

    public void ToAdmin(Message message, SessionID sessionID) { }

    public void FromAdmin(Message message, SessionID sessionID) { }

    public void ToApp(Message message, SessionID sessionID) { }

    public void Dispose() => Stop();

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public static class TestHost
{
    // Porta livre na hora do teste; ninguém escuta nela até alguém subir algo.
    public static int FreePort()
    {
        var freePortProbe = new TcpListener(IPAddress.Loopback, 0);
        freePortProbe.Start();
        var freePort = ((IPEndPoint)freePortProbe.LocalEndpoint).Port;
        freePortProbe.Stop();
        return freePort;
    }

    public static WebApplicationFactory<Program> CreateOrderGeneratorFactory(int fixPort, string accumulatorUrl = "http://127.0.0.1:1", string? webRoot = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(webHostBuilder =>
        {
            webHostBuilder.UseSetting("Fix:AcceptorHost", "127.0.0.1");
            webHostBuilder.UseSetting("Fix:AcceptorPort", fixPort.ToString());
            webHostBuilder.UseSetting("OrderAccumulator:BaseUrl", accumulatorUrl);
            if (webRoot is not null)
                webHostBuilder.UseSetting(WebHostDefaults.WebRootKey, webRoot);
        });

    public static async Task WaitUntil(Func<bool> expectedCondition)
    {
        var conditionClock = Stopwatch.StartNew();
        while (!expectedCondition())
        {
            Assert.True(conditionClock.Elapsed < TimeSpan.FromSeconds(10), "a condição esperada não aconteceu em 10 s");
            await Task.Delay(50);
        }
    }
}
