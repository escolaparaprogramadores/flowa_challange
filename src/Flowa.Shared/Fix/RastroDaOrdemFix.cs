using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Flowa.Shared.Fix;

// Rastro de uma ordem entre o OrderGenerator e o OrderAccumulator. O contexto W3C (traceparent)
// viaja na tag 5100 da NewOrderSingle. Com DD_TRACE_OTEL_ENABLED=true, o tracer do Datadog junta
// estes spans aos dele no mesmo rastro; sem quem escute a fonte, nenhum span é criado.
public static class RastroDaOrdemFix
{
    public const int TagTraceParent = 5100;
    public const string NomeDaFonteDoRastro = "Flowa.Fix";

    private static readonly ActivitySource FonteDoRastro = new(NomeDaFonteDoRastro);

    public static Activity? IniciarEnvioDaOrdem() =>
        FonteDoRastro.StartActivity("fix.envio_da_ordem", ActivityKind.Producer);

    public static string? TraceParentDoEnvio(Activity? envioDaOrdem) =>
        envioDaOrdem is { IdFormat: ActivityIdFormat.W3C } ? envioDaOrdem.Id : null;

    // Valor ausente ou fora do formato W3C não muda nada na ordem: o recebimento só abre um rastro novo.
    public static Activity? IniciarRecebimentoDaOrdem(string? traceParentRecebido)
    {
        ActivityContext.TryParse(traceParentRecebido, null, out var contextoDoEnvio);
        return FonteDoRastro.StartActivity("fix.recebimento_da_ordem", ActivityKind.Consumer, contextoDoEnvio);
    }

    // O log da sessão FIX escreve a mensagem crua, e o traceparent da 5100 leva o trace id, que não vai
    // ao log (decisão 17). A tag continua na linha, só o valor some; a mensagem enviada não muda.
    public static string OcultarTraceParentNoLog(string linhaDoLogFix) =>
        linhaDoLogFix.Contains(PrefixoDaTagTraceParent, StringComparison.Ordinal)
            ? ValorDaTagTraceParent.Replace(linhaDoLogFix, PrefixoDaTagTraceParent + ValorOcultoNoLog)
            : linhaDoLogFix;

    public const string ValorOcultoNoLog = "***";
    private const string PrefixoDaTagTraceParent = "5100=";
    private static readonly Regex ValorDaTagTraceParent = new("(?<=^|\u0001)5100=[^\u0001]*", RegexOptions.CultureInvariant);
}
