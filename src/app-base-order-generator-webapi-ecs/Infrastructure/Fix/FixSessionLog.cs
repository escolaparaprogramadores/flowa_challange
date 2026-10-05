using System.Diagnostics;
using System.Text.RegularExpressions;
using Base.OrderGenerator.Commons;
using QuickFix.Logger;

namespace Base.OrderGenerator.Infrastructure.Fix;

// One JSON line per FIX message, with SOH shown as "|". Heartbeats (35=0) are not logged (decision 22).
public sealed partial class FixSessionLog(IApplicationLogger<FixSessionLog> fixSessionLogger, string? fixSessionId) : ILog
{
    private const char FixFieldSeparator = '\u0001';
    private const string HeartbeatMessageTypeField = "\u000135=0\u0001";

    public void OnIncoming(string incomingFixMessage) => LogFixMessage("FIX message received.", incomingFixMessage);

    public void OnOutgoing(string outgoingFixMessage) => LogFixMessage("FIX message sent.", outgoingFixMessage);

    public void OnEvent(string fixSessionEvent) => fixSessionLogger.LogInformation(fixSessionEvent, new { FixSession = fixSessionId });

    public void Clear() { }

    public void Dispose() { }

    private void LogFixMessage(string logMessage, string fixMessage)
    {
        if (fixMessage.Contains(HeartbeatMessageTypeField, StringComparison.Ordinal))
            return;

        fixSessionLogger.LogInformation(logMessage, new
        {
            FixSession = fixSessionId,
            FixMessage = fixMessage.Replace(FixFieldSeparator, '|'),
            TraceId = ReadOrderTraceIdOutsideSpan(fixMessage)
        });
    }

    // A message read by the QuickFIX thread is logged outside any span, so the framework adds no TraceId.
    // An order message carries its trace id anyway: the ClOrdID (tag 11) is the trace id (decision 21).
    private static string? ReadOrderTraceIdOutsideSpan(string fixMessage)
    {
        if (Activity.Current is not null)
            return null;

        var clOrdIdMatch = ClOrdIdTraceIdPattern().Match(fixMessage);
        return clOrdIdMatch.Success ? clOrdIdMatch.Groups["clOrdId"].Value : null;
    }

    [GeneratedRegex("\u000111=(?<clOrdId>[0-9a-f]{32})\u0001", RegexOptions.CultureInvariant)]
    private static partial Regex ClOrdIdTraceIdPattern();
}
