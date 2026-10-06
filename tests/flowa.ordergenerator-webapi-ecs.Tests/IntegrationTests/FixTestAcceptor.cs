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

// The tests start FIX sessions with the same SessionID; QuickFIX keeps the sessions in a static
// registry, so two classes running together would fight over the same name.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Flowa.OrderGenerator.Tests;

// Plays the OrderAccumulator only in what the OrderGenerator sees: accepts the FIX session,
// keeps the received NewOrderSingle messages and answers with the ExecutionReport the test chooses.
public sealed class FixTestAcceptor : IApplication, IDisposable
{
    private ThreadedSocketAcceptor? _threadedFixAcceptor;
    private TaskCompletionSource _acceptorLogon = NewFixLogonSignal();
    private int _executionReportNumber;

    public FixTestAcceptor(int fixAcceptorPort) => AcceptorPort = fixAcceptorPort;

    public int AcceptorPort { get; }

    public ConcurrentQueue<Message> ReceivedOrders { get; } = new();

    // Each ExecutionReport sent back, by the ClOrdID it carries.
    public ConcurrentDictionary<string, Message> SentExecutionReports { get; } = new();

    // Builds the answer to the received order; null leaves the order without an answer (silent acceptor).
    public Func<Message, Message?> ExecutionReportResponder { get; set; } = receivedOrder => null;

    // How long to wait before sending the answer, per order.
    public Func<Message, TimeSpan> ExecutionReportDelay { get; set; } = receivedOrder => TimeSpan.Zero;

    // Extra report sent before the answer, with a ClOrdID that is not the one of the order.
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
            DataDictionary={Path.Combine(AppContext.BaseDirectory, "FIX44-flowa.xml")}
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

    // Waits for both ends: the acceptor received the Logon and the initiator already received its answer.
    public async Task WaitForFixSessionLogonAsync()
    {
        await _acceptorLogon.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var initiatorSessionId = new SessionID("FIX.4.4", "ORDERGENERATOR", "ORDERACCUMULATOR");
        var fixLogonClock = Stopwatch.StartNew();
        while (Session.LookupSession(initiatorSessionId)?.IsLoggedOn != true)
        {
            if (fixLogonClock.Elapsed > TimeSpan.FromSeconds(15))
                throw new TimeoutException("The initiator did not log on.");
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
            SendAnswerToOrder(strayExecutionReport, orderGeneratorFixSessionId);

        var executionReport = ExecutionReportResponder(receivedFixApplicationMessage);
        if (executionReport is null)
            return;

        var executionReportDelay = ExecutionReportDelay(receivedFixApplicationMessage);
        if (executionReportDelay == TimeSpan.Zero)
            SendAnswerToOrder(executionReport, orderGeneratorFixSessionId);
        else
            // Off the session thread, so the other orders keep arriving while this one waits.
            _ = Task.Delay(executionReportDelay).ContinueWith(_ => SendAnswerToOrder(executionReport, orderGeneratorFixSessionId), TaskScheduler.Default);
    }

    // Session Reject (35=3) pointing at the sequence number of the received order.
    public Message BuildSessionReject(Message receivedOrder, string? rejectText, int? sessionRejectReason)
    {
        var sessionReject = new QuickFix.FIX44.Reject(new RefSeqNum(receivedOrder.Header.GetULong(Tags.MsgSeqNum)));
        if (rejectText is not null)
            sessionReject.SetField(new Text(rejectText));
        if (sessionRejectReason is not null)
            sessionReject.SetField(new SessionRejectReason(sessionRejectReason.Value));
        return sessionReject;
    }

    // BusinessMessageReject (35=j): by ClOrdID in BusinessRejectRefID (379) or, without it, by RefSeqNum (45).
    public Message BuildBusinessMessageReject(Message receivedOrder, string? rejectText, int businessRejectReason, string? businessRejectRefId)
    {
        var businessMessageReject = new QuickFix.FIX44.BusinessMessageReject(new RefMsgType(MsgType.NEW_ORDER_D), new BusinessRejectReason(businessRejectReason));
        if (businessRejectRefId is not null)
            businessMessageReject.SetField(new BusinessRejectRefID(businessRejectRefId));
        else
            businessMessageReject.SetField(new RefSeqNum(receivedOrder.Header.GetULong(Tags.MsgSeqNum)));
        if (rejectText is not null)
            businessMessageReject.SetField(new Text(rejectText));
        return businessMessageReject;
    }

    // Logout (35=5) sent right away; Session.Logout would only send it on the next session tick.
    public bool SendLogoutToOrderGenerator() =>
        Session.SendToTarget(new QuickFix.FIX44.Logout(), new SessionID("FIX.4.4", "ORDERACCUMULATOR", "ORDERGENERATOR"));

    private void SendAnswerToOrder(Message orderAnswer, SessionID orderGeneratorFixSessionId)
    {
        if (orderAnswer.Header.GetString(Tags.MsgType) == MsgType.EXECUTION_REPORT)
            SentExecutionReports[orderAnswer.GetString(Tags.ClOrdID)] = orderAnswer;
        Session.SendToTarget(orderAnswer, orderGeneratorFixSessionId);
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
    // A port free at test time; nobody listens on it until something starts on it.
    public static int FindFreeTcpPort()
    {
        var freeTcpPortProbe = new TcpListener(IPAddress.Loopback, 0);
        freeTcpPortProbe.Start();
        var freeTcpPort = ((IPEndPoint)freeTcpPortProbe.LocalEndpoint).Port;
        freeTcpPortProbe.Stop();
        return freeTcpPort;
    }

    public static WebApplicationFactory<Program> CreateOrderGeneratorFactory(int fixAcceptorPort, string accumulatorBaseUrl = "http://127.0.0.1:1",
        string? orderGeneratorWebRoot = null, string? executionReportTimeoutSeconds = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(orderGeneratorWebHostBuilder =>
        {
            if (executionReportTimeoutSeconds is not null)
                orderGeneratorWebHostBuilder.UseSetting("Fix:ExecutionReportTimeoutSeconds", executionReportTimeoutSeconds);
            // In Development ASP.NET puts the project wwwroot (static web assets) in front of the test root;
            // with the screen build present, the real index.html would win. Production is how the app really runs.
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
            Assert.True(testConditionClock.Elapsed < TimeSpan.FromSeconds(10), "the expected condition did not happen in 10 s");
            await Task.Delay(50);
        }
    }
}
