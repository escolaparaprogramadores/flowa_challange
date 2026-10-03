using System.Diagnostics;

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
}
