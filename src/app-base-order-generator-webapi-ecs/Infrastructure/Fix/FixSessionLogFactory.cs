using Base.OrderGenerator.Commons.Logging;
using QuickFix;
using QuickFix.Logger;

namespace Base.OrderGenerator.Infrastructure.Fix;

public sealed class FixSessionLogFactory(IApplicationLogger<FixSessionLog> fixSessionLogger) : ILogFactory
{
    public ILog Create(SessionID fixSessionId) => new FixSessionLog(fixSessionLogger, fixSessionId.ToString());

    public ILog CreateNonSessionLog() => new FixSessionLog(fixSessionLogger, null);
}
