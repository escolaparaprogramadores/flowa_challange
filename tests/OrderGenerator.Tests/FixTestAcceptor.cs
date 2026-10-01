using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
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
public sealed class FixTestAcceptor : IApplication, IDisposable
{
    private ThreadedSocketAcceptor? _threadedFixAcceptor;
    private TaskCompletionSource _acceptorLogon = NewFixLogonSignal();
    private int _executionReportNumber;

    public FixTestAcceptor(int fixAcceptorPort) => AcceptorPort = fixAcceptorPort;

    public int AcceptorPort { get; }

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
        ExecutionReportResponder = BuildAcceptedExecutionReport;
        ExecutionReportDelay = receivedOrder => TimeSpan.Zero;
        StrayExecutionReport = receivedOrder => null;
        ReceivedOrders.Clear();
        SentExecutionReports.Clear();
    }

    public void StartFixTestAcceptor()
    {
        _acceptorLogon = NewFixLogonSignal();
        var acceptorSettings = new SessionSettings(new StringReader($"""
            [DEFAULT]
            ConnectionType=acceptor
            SocketAcceptHost=127.0.0.1
            SocketAcceptPort={AcceptorPort}
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
        _threadedFixAcceptor = new ThreadedSocketAcceptor(this, new MemoryStoreFactory(), acceptorSettings, new NullLogFactory(), new DefaultMessageFactory());
        _threadedFixAcceptor.Start();
    }

    public void StopFixTestAcceptor()
    {
        _threadedFixAcceptor?.Stop(true);
        _threadedFixAcceptor?.Dispose();
        _threadedFixAcceptor = null;
    }

    // Espera as duas pontas: o acceptor recebeu o Logon e o initiator já recebeu a resposta dele.
    public async Task WaitForFixSessionLogonAsync()
    {
        await _acceptorLogon.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var initiatorSessionId = new SessionID("FIX.4.4", "ORDERGENERATOR", "ORDERACCUMULATOR");
        var fixLogonClock = Stopwatch.StartNew();
        while (Session.LookupSession(initiatorSessionId)?.IsLoggedOn != true)
        {
            if (fixLogonClock.Elapsed > TimeSpan.FromSeconds(15))
                throw new TimeoutException("O initiator não logou.");
            await Task.Delay(50);
        }
    }

    public Message BuildAcceptedExecutionReport(Message receivedOrder) =>
        BuildExecutionReport(receivedOrder, ExecType.NEW, OrdStatus.NEW, receivedOrder.GetDecimal(Tags.OrderQty));

    public Message BuildRejectedExecutionReport(Message receivedOrder, string rejectionText)
    {
        var rejectedExecutionReport = BuildExecutionReport(receivedOrder, ExecType.REJECTED, OrdStatus.REJECTED, 0);
        rejectedExecutionReport.SetField(new Text(rejectionText));
        return rejectedExecutionReport;
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

    public void FromApp(Message receivedFixApplicationMessage, SessionID orderGeneratorFixSessionId)
    {
        if (receivedFixApplicationMessage.Header.GetString(Tags.MsgType) != MsgType.NEW_ORDER_D)
            return;

        ReceivedOrders.Enqueue(receivedFixApplicationMessage);
        var strayExecutionReport = StrayExecutionReport(receivedFixApplicationMessage);
        if (strayExecutionReport is not null)
            SendExecutionReport(strayExecutionReport, orderGeneratorFixSessionId);

        var executionReport = ExecutionReportResponder(receivedFixApplicationMessage);
        if (executionReport is null)
            return;

        var executionReportDelay = ExecutionReportDelay(receivedFixApplicationMessage);
        if (executionReportDelay == TimeSpan.Zero)
            SendExecutionReport(executionReport, orderGeneratorFixSessionId);
        else
            // Fora da thread da sessão, para as outras ordens seguirem chegando enquanto esta espera.
            _ = Task.Delay(executionReportDelay).ContinueWith(_ => SendExecutionReport(executionReport, orderGeneratorFixSessionId), TaskScheduler.Default);
    }

    private void SendExecutionReport(Message executionReport, SessionID orderGeneratorFixSessionId)
    {
        SentExecutionReports[executionReport.GetString(Tags.ClOrdID)] = executionReport;
        Session.SendToTarget(executionReport, orderGeneratorFixSessionId);
    }

    public void OnLogon(SessionID orderGeneratorFixSessionId) => _acceptorLogon.TrySetResult();

    public void OnCreate(SessionID orderGeneratorFixSessionId) { }

    public void OnLogout(SessionID orderGeneratorFixSessionId) { }

    public void ToAdmin(Message outgoingFixAdminMessage, SessionID orderGeneratorFixSessionId) { }

    public void FromAdmin(Message incomingFixAdminMessage, SessionID orderGeneratorFixSessionId) { }

    public void ToApp(Message outgoingFixApplicationMessage, SessionID orderGeneratorFixSessionId) { }

    public void Dispose() => StopFixTestAcceptor();

    private static TaskCompletionSource NewFixLogonSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public static class OrderGeneratorTestHost
{
    // Porta livre na hora do teste; ninguém escuta nela até alguém subir algo.
    public static int FindFreeTcpPort()
    {
        var freeTcpPortProbe = new TcpListener(IPAddress.Loopback, 0);
        freeTcpPortProbe.Start();
        var freeTcpPort = ((IPEndPoint)freeTcpPortProbe.LocalEndpoint).Port;
        freeTcpPortProbe.Stop();
        return freeTcpPort;
    }

    public static WebApplicationFactory<Program> CreateOrderGeneratorFactory(int fixAcceptorPort, string accumulatorBaseUrl = "http://127.0.0.1:1", string? orderGeneratorWebRoot = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(orderGeneratorWebHostBuilder =>
        {
            // Em Development o ASP.NET põe o wwwroot do projeto (static web assets) na frente da raiz do teste;
            // com o build da tela presente, o index.html real venceria. Production é como o app roda de verdade.
            orderGeneratorWebHostBuilder.UseEnvironment(Environments.Production);
            orderGeneratorWebHostBuilder.UseSetting("Fix:AcceptorHost", "127.0.0.1");
            orderGeneratorWebHostBuilder.UseSetting("Fix:AcceptorPort", fixAcceptorPort.ToString());
            orderGeneratorWebHostBuilder.UseSetting("OrderAccumulator:BaseUrl", accumulatorBaseUrl);
            if (orderGeneratorWebRoot is not null)
                orderGeneratorWebHostBuilder.UseSetting(WebHostDefaults.WebRootKey, orderGeneratorWebRoot);
        });

    public static async Task WaitUntilTestConditionHolds(Func<bool> expectedCondition)
    {
        var testConditionClock = Stopwatch.StartNew();
        while (!expectedCondition())
        {
            Assert.True(testConditionClock.Elapsed < TimeSpan.FromSeconds(10), "a condição esperada não aconteceu em 10 s");
            await Task.Delay(50);
        }
    }
}
