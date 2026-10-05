using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using Base.OrderGenerator.Infrastructure.Fix;
using QuickFix.Fields;

namespace Base.OrderGenerator.Tests;

// CA-O5 of wave 3, OrderGenerator side: the order goes out with the traceparent of the sending span in tag 5100.
public sealed class OrderTraceOnSendingTests : IClassFixture<LoggedOnOrderGenerator>
{
    private const string BuyPetr4OrderJson = """{"symbol":"PETR4","side":"buy","quantity":100,"price":10.50}""";

    private readonly LoggedOnOrderGenerator _loggedOnOrderGenerator;

    public OrderTraceOnSendingTests(LoggedOnOrderGenerator loggedOnOrderGenerator)
    {
        _loggedOnOrderGenerator = loggedOnOrderGenerator;
        _loggedOnOrderGenerator.FixAcceptor.ResetToAcceptEveryOrder();
    }

    [Fact]
    public async Task Sent_order_carries_in_tag_5100_the_traceparent_of_the_sending_span()
    {
        var orderSendingSpans = new ConcurrentQueue<Activity>();
        using var orderTraceListener = ListenToOrderSendingSpans(orderSendingSpans);

        var orderHttpResponse = await PostBuyPetr4Order();

        Assert.Equal(HttpStatusCode.OK, orderHttpResponse.StatusCode);
        var orderSending = Assert.Single(orderSendingSpans);
        var orderReceivedByAcceptor = Assert.Single(_loggedOnOrderGenerator.FixAcceptor.ReceivedOrders);
        Assert.Equal($"00-{orderSending.TraceId}-{orderSending.SpanId}-01", orderReceivedByAcceptor.GetString(FixOrderTraceProvider.TraceParentTag));
        Assert.Equal(ActivityKind.Producer, orderSending.Kind);
        // No tag on the span: span names and shape stay as they were (CA-34).
        Assert.Empty(orderSending.TagObjects);
        // R-01: the context does not use Text (58).
        Assert.False(orderReceivedByAcceptor.IsSetField(Tags.Text));
    }

    [Fact]
    public async Task With_nobody_listening_to_the_trace_the_order_goes_out_without_tag_5100()
    {
        var orderHttpResponse = await PostBuyPetr4Order();

        Assert.Equal(HttpStatusCode.OK, orderHttpResponse.StatusCode);
        var orderReceivedByAcceptor = Assert.Single(_loggedOnOrderGenerator.FixAcceptor.ReceivedOrders);
        Assert.False(orderReceivedByAcceptor.IsSetField(FixOrderTraceProvider.TraceParentTag));
    }

    // RN-01: the ClOrdID is the trace id, so a traceparent sent by the caller cannot be continued, or two orders of
    // the same caller trace would get the same ClOrdID and the second would be answered as a repeat of the first.
    [Fact]
    public async Task Two_orders_with_the_same_caller_traceparent_get_different_clordids_from_new_traces()
    {
        var orderSendingSpans = new ConcurrentQueue<Activity>();
        using var orderTraceListener = ListenToOrderSendingSpans(orderSendingSpans);
        const string callerTraceId = "0af7651916cd43dd8448eb211c80319c";
        const string callerTraceParent = $"00-{callerTraceId}-b7ad6b7169203331-01";

        var firstOrderResponse = await PostBuyPetr4OrderWithCallerTraceParent(callerTraceParent);
        var secondOrderResponse = await PostBuyPetr4OrderWithCallerTraceParent(callerTraceParent);

        var firstClOrdId = (await OrderApiTests.ReadOrderDataAsync(firstOrderResponse)).GetProperty("clOrdId").GetString();
        var secondClOrdId = (await OrderApiTests.ReadOrderDataAsync(secondOrderResponse)).GetProperty("clOrdId").GetString();
        Assert.NotEqual(firstClOrdId, secondClOrdId);
        Assert.NotEqual(callerTraceId, firstClOrdId);
        Assert.NotEqual(callerTraceId, secondClOrdId);
        Assert.Equal([firstClOrdId, secondClOrdId], orderSendingSpans.Select(orderSending => orderSending.TraceId.ToHexString()));
        Assert.Equal(2, _loggedOnOrderGenerator.FixAcceptor.ReceivedOrders.Count);
    }

    private Task<HttpResponseMessage> PostBuyPetr4Order() =>
        _loggedOnOrderGenerator.OrderGeneratorClient.PostAsync("/api/orders", new StringContent(BuyPetr4OrderJson, Encoding.UTF8, "application/json"));

    private Task<HttpResponseMessage> PostBuyPetr4OrderWithCallerTraceParent(string callerTraceParent)
    {
        var orderHttpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = new StringContent(BuyPetr4OrderJson, Encoding.UTF8, "application/json")
        };
        orderHttpRequest.Headers.Add("traceparent", callerTraceParent);
        return _loggedOnOrderGenerator.OrderGeneratorClient.SendAsync(orderHttpRequest);
    }

    private static ActivityListener ListenToOrderSendingSpans(ConcurrentQueue<Activity> orderSendingSpans)
    {
        var orderTraceListener = new ActivityListener
        {
            ShouldListenTo = traceSource => traceSource.Name == FixOrderTraceProvider.TraceSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = startedSpan =>
            {
                if (startedSpan.OperationName == "fix.envio_da_ordem")
                    orderSendingSpans.Enqueue(startedSpan);
            }
        };
        ActivitySource.AddActivityListener(orderTraceListener);
        return orderTraceListener;
    }
}
