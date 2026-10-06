using Base.OrderAccumulator.Commons.Logging;
using QuickFix.Logger;
using QuickFix;

namespace Base.OrderAccumulator.Infrastructure.Fix;

public sealed class FixSessionLogFactory : ILogFactory
{
    private readonly IApplicationLogger<FixSessionLog> fixSessionLogger;

    public FixSessionLogFactory(IApplicationLogger<FixSessionLog> fixSessionLogger)
    {
        this.fixSessionLogger = fixSessionLogger ?? throw new ArgumentNullException(nameof(fixSessionLogger));
    }

    public ILog Create(SessionID fixSessionId) => new FixSessionLog(fixSessionLogger, fixSessionId.ToString());

    public ILog CreateNonSessionLog() => new FixSessionLog(fixSessionLogger, null);
}
