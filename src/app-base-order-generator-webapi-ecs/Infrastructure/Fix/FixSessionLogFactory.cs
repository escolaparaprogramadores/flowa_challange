using Base.OrderGenerator.Commons;
using QuickFix;
using QuickFix.Logger;

namespace Base.OrderGenerator.Infrastructure.Fix;

// Gives QuickFIX a session log that writes through the application logger, so FIX lines are JSON too.
public sealed class FixSessionLogFactory(IApplicationLogger<FixSessionLog> fixSessionLogger) : ILogFactory
{
    public ILog Create(SessionID fixSessionId) => new FixSessionLog(fixSessionLogger, fixSessionId.ToString());

    public ILog CreateNonSessionLog() => new FixSessionLog(fixSessionLogger, null);
}
