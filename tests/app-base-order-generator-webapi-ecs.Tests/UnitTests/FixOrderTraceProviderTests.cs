using System.Diagnostics;
using Base.OrderGenerator.Infrastructure.Fix;

namespace Base.OrderGenerator.Tests;

// Decision 21: the order number is the 128-bit trace id of the sending span; with no span, a random GUID.
public class FixOrderTraceProviderTests
{
    [Fact]
    public void ClOrdId_is_the_32_hex_trace_id_of_the_sending_span()
    {
        using var orderSending = new Activity("fix.envio_da_ordem").SetIdFormat(ActivityIdFormat.W3C).Start();

        var clOrdId = FixOrderTraceProvider.CreateClOrdId(orderSending);

        Assert.Equal(orderSending.TraceId.ToHexString(), clOrdId);
        Assert.Matches("^[0-9a-f]{32}$", clOrdId);
    }

    [Fact]
    public void Without_a_sending_span_the_clordid_is_a_new_random_guid_each_time()
    {
        var firstClOrdId = FixOrderTraceProvider.CreateClOrdId(null);
        var secondClOrdId = FixOrderTraceProvider.CreateClOrdId(null);

        Assert.Matches("^[0-9a-f]{32}$", firstClOrdId);
        Assert.True(Guid.TryParseExact(firstClOrdId, "N", out _));
        Assert.NotEqual(firstClOrdId, secondClOrdId);
    }

    [Fact]
    public void Span_with_a_hierarchical_id_is_not_a_trace_and_gives_a_random_guid()
    {
        using var hierarchicalSpan = new Activity("fix.envio_da_ordem").SetIdFormat(ActivityIdFormat.Hierarchical).Start();

        var clOrdId = FixOrderTraceProvider.CreateClOrdId(hierarchicalSpan);

        Assert.True(Guid.TryParseExact(clOrdId, "N", out _));
        Assert.NotEqual(hierarchicalSpan.TraceId.ToHexString(), clOrdId);
    }
}
