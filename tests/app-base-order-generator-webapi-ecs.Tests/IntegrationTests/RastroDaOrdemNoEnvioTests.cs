using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using Base.OrderGenerator.Infrastructure.Fix;
using QuickFix.Fields;

namespace Base.OrderGenerator.Tests;

// CA-O5 da onda 3, ponta do OrderGenerator: a ordem sai com o traceparent do span de envio na tag 5100.
public sealed class RastroDaOrdemNoEnvioTests : IClassFixture<LoggedOnOrderGenerator>
{
    private readonly LoggedOnOrderGenerator _loggedOnOrderGenerator;

    public RastroDaOrdemNoEnvioTests(LoggedOnOrderGenerator loggedOnOrderGenerator)
    {
        _loggedOnOrderGenerator = loggedOnOrderGenerator;
        _loggedOnOrderGenerator.FixAcceptor.ResetToAcceptEveryOrder();
    }

    [Fact]
    public async Task Ordem_enviada_leva_na_tag_5100_o_traceparent_do_span_de_envio()
    {
        var enviosDaOrdem = new ConcurrentQueue<Activity>();
        using var ouvinteDoRastroDaOrdem = OuvirEnviosDaOrdem(enviosDaOrdem);

        var respostaDaOrdem = await EnviarOrdemDeCompraDePetr4();

        Assert.Equal(HttpStatusCode.OK, respostaDaOrdem.StatusCode);
        var envioDaOrdem = Assert.Single(enviosDaOrdem);
        var ordemRecebidaPeloAcceptor = Assert.Single(_loggedOnOrderGenerator.FixAcceptor.ReceivedOrders);
        Assert.Equal($"00-{envioDaOrdem.TraceId}-{envioDaOrdem.SpanId}-01", ordemRecebidaPeloAcceptor.GetString(FixOrderTraceProvider.TraceParentTag));
        Assert.Equal(ActivityKind.Producer, envioDaOrdem.Kind);
        // Nenhuma etiqueta no span: nome e forma do span não mudam (CA-34).
        Assert.Empty(envioDaOrdem.TagObjects);
        // R-01: o contexto não usa o Text (58).
        Assert.False(ordemRecebidaPeloAcceptor.IsSetField(Tags.Text));
    }

    [Fact]
    public async Task Sem_ninguem_ouvindo_o_rastro_a_ordem_sai_sem_a_tag_5100()
    {
        var respostaDaOrdem = await EnviarOrdemDeCompraDePetr4();

        Assert.Equal(HttpStatusCode.OK, respostaDaOrdem.StatusCode);
        var ordemRecebidaPeloAcceptor = Assert.Single(_loggedOnOrderGenerator.FixAcceptor.ReceivedOrders);
        Assert.False(ordemRecebidaPeloAcceptor.IsSetField(FixOrderTraceProvider.TraceParentTag));
    }

    private Task<HttpResponseMessage> EnviarOrdemDeCompraDePetr4() =>
        _loggedOnOrderGenerator.OrderGeneratorClient.PostAsync("/api/orders",
            new StringContent("""{"symbol":"PETR4","side":"buy","quantity":100,"price":10.50}""", Encoding.UTF8, "application/json"));

    private static ActivityListener OuvirEnviosDaOrdem(ConcurrentQueue<Activity> enviosDaOrdem)
    {
        var ouvinteDoRastroDaOrdem = new ActivityListener
        {
            ShouldListenTo = fonteDoRastro => fonteDoRastro.Name == FixOrderTraceProvider.TraceSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = spanIniciado =>
            {
                if (spanIniciado.OperationName == "fix.envio_da_ordem")
                    enviosDaOrdem.Enqueue(spanIniciado);
            }
        };
        ActivitySource.AddActivityListener(ouvinteDoRastroDaOrdem);
        return ouvinteDoRastroDaOrdem;
    }
}
