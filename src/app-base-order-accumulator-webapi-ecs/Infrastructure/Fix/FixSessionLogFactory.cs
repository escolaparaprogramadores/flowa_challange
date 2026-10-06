using Base.OrderAccumulator.Commons.Logging;
using QuickFix.Logger;
using QuickFix;

namespace Base.OrderAccumulator.Infrastructure.Fix;

public sealed class FixSessionLogFactory(IApplicationLogger<FixSessionLog> fixSessionLogger) : ILogFactory
{
    public ILog Create(SessionID fixSessionId) => new FixSessionLog(fixSessionLogger, fixSessionId.ToString());

    public ILog CreateNonSessionLog() => new FixSessionLog(fixSessionLogger, null);
}
