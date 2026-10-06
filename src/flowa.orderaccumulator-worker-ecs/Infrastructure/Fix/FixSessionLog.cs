using System.Diagnostics;
using System.Text.RegularExpressions;
using Flowa.Commons.Logging;
using QuickFix.Logger;

namespace Flowa.OrderAccumulator.Infrastructure.Fix;

public sealed class FixSessionLog : ILog
{
    private const char FixFieldSeparator = '\u0001';
    private const string HeartbeatMessageTypeField = "\u000135=0\u0001";

    private static readonly Regex ClOrdIdTraceIdPattern = new("\u000111=(?<clOrdId>[0-9a-f]{32})\u0001", RegexOptions.CultureInvariant);

    private readonly IApplicationLogger<FixSessionLog> fixSessionLogger;
    private readonly string? fixSessionId;

    public FixSessionLog(IApplicationLogger<FixSessionLog> fixSessionLogger, string? fixSessionId)
    {
        this.fixSessionLogger = fixSessionLogger ?? throw new ArgumentNullException(nameof(fixSessionLogger));
        this.fixSessionId = fixSessionId;
    }

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

    private static string? ReadOrderTraceIdOutsideSpan(string fixMessage)
    {
        if (Activity.Current is not null)
            return null;

        var clOrdIdMatch = ClOrdIdTraceIdPattern.Match(fixMessage);
        return clOrdIdMatch.Success ? clOrdIdMatch.Groups["clOrdId"].Value : null;
    }
}
