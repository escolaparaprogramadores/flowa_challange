using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Flowa.Shared.Fix;

// Rastro de uma ordem entre o OrderGenerator e o OrderAccumulator. O contexto W3C (traceparent)
// viaja na tag 5100 da NewOrderSingle. Com DD_TRACE_OTEL_ENABLED=true, o tracer do Datadog junta
// estes spans aos dele no mesmo rastro; sem quem escute a fonte, nenhum span é criado.
public static class FixOrderTraceProvider
{
    public const int TraceParentTag = 5100;
    public const string TraceSourceName = "Flowa.Fix";

    private static readonly ActivitySource OrderTraceSource = new(TraceSourceName);

    public static Activity? StartOrderSending() =>
        OrderTraceSource.StartActivity("fix.envio_da_ordem", ActivityKind.Producer);

    public static string? GetTraceParentOfOrderSending(Activity? orderSending) =>
        orderSending is { IdFormat: ActivityIdFormat.W3C } ? orderSending.Id : null;

    // Valor ausente ou fora do formato W3C não muda nada na ordem: o recebimento só abre um rastro novo.
    public static Activity? StartOrderReceiving(string? receivedTraceParent)
    {
        ActivityContext.TryParse(receivedTraceParent, null, out var orderSendingContext);
        return OrderTraceSource.StartActivity("fix.recebimento_da_ordem", ActivityKind.Consumer, orderSendingContext);
    }

    // O log da sessão FIX escreve a mensagem crua, e o traceparent da 5100 leva o trace id, que não vai
    // ao log (decisão 17). A tag continua na linha, só o valor some; a mensagem enviada não muda.
    public static string HideTraceParentInLog(string fixLogLine) =>
        fixLogLine.Contains(TraceParentTagPrefix, StringComparison.Ordinal)
            ? TraceParentTagValuePattern.Replace(fixLogLine, TraceParentTagPrefix + HiddenLogValue)
            : fixLogLine;

    public const string HiddenLogValue = "***";
    private const string TraceParentTagPrefix = "5100=";
    private static readonly Regex TraceParentTagValuePattern = new("(?<=^|\u0001)5100=[^\u0001]*", RegexOptions.CultureInvariant);
}
