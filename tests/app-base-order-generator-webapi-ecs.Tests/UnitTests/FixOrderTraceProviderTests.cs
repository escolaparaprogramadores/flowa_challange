using Base.OrderGenerator.Infrastructure.Fix;

namespace Base.OrderGenerator.Tests;

// Decision 17: the trace id that travels in tag 5100 stays out of the FIX session log.
public class FixOrderTraceProviderTests
{
    private const string TraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";

    [Fact]
    public void Line_with_5100_in_the_middle_keeps_the_tag_and_loses_only_the_value()
    {
        var fixLogLine = $"8=FIX.4.4\u000135=D\u000111=abc\u00015100={TraceParent}\u000110=128\u0001";

        Assert.Equal("8=FIX.4.4\u000135=D\u000111=abc\u00015100=***\u000110=128\u0001", FixOrderTraceProvider.HideTraceParentInLog(fixLogLine));
    }

    [Fact]
    public void Line_that_starts_with_5100_also_loses_the_value()
    {
        Assert.Equal("5100=***\u000110=128", FixOrderTraceProvider.HideTraceParentInLog($"5100={TraceParent}\u000110=128"));
    }

    [Fact]
    public void Another_tag_ending_in_5100_and_values_with_5100_stay_the_same()
    {
        var fixLogLine = "8=FIX.4.4\u000115100=x\u000158=5100=y\u000110=128\u0001";

        Assert.Equal(fixLogLine, FixOrderTraceProvider.HideTraceParentInLog(fixLogLine));
    }

    [Fact]
    public void Line_without_5100_comes_back_as_the_same_instance()
    {
        var fixLogLine = "8=FIX.4.4\u000135=A\u000110=213\u0001";

        Assert.Same(fixLogLine, FixOrderTraceProvider.HideTraceParentInLog(fixLogLine));
    }
}
