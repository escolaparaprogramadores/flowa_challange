using Flowa.Commons.Logging;
using QuickFix;
using QuickFix.Logger;

namespace Flowa.OrderGenerator.Infrastructure.Fix;

internal sealed class FixSessionLogFactory : ILogFactory
{
    private readonly IApplicationLogger<FixSessionLog> _fixSessionLogger;

    public FixSessionLogFactory(IApplicationLogger<FixSessionLog> fixSessionLogger)
    {
        _fixSessionLogger = fixSessionLogger ?? throw new ArgumentNullException(nameof(fixSessionLogger));
    }

    public ILog Create(SessionID fixSessionId) => new FixSessionLog(_fixSessionLogger, fixSessionId.ToString());

    public ILog CreateNonSessionLog() => new FixSessionLog(_fixSessionLogger, null);
}
