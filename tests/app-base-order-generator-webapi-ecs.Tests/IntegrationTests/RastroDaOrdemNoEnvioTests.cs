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
        // Decisão 17: nenhuma etiqueta no span, e o ClOrdID em particular nunca.
        Assert.Empty(envioDaOrdem.TagObjects);
        // R-01: o contexto não usa o Text (58).
        Assert.False(ordemRecebidaPeloAcceptor.IsSetField(Tags.Text));
    }

    [Fact]
    public async Task Log_da_sessao_FIX_mostra_a_tag_5100_sem_o_trace_id()
    {
        var enviosDaOrdem = new ConcurrentQueue<Activity>();
        using var ouvinteDoRastroDaOrdem = OuvirEnviosDaOrdem(enviosDaOrdem);
        // O ScreenLog do QuickFIX escreve no stdout, que é o que o docker compose logs e o CloudWatch recebem.
        var stdoutOriginal = Console.Out;
        var stdoutCapturado = new StringWriter();
        Console.SetOut(TextWriter.Synchronized(stdoutCapturado));
        HttpResponseMessage respostaDaOrdem;
        try
        {
            respostaDaOrdem = await EnviarOrdemDeCompraDePetr4();
        }
        finally
        {
            Console.SetOut(stdoutOriginal);
        }

        Assert.Equal(HttpStatusCode.OK, respostaDaOrdem.StatusCode);
        var envioDaOrdem = Assert.Single(enviosDaOrdem);
        // A mensagem que saiu leva o traceparent inteiro; só o log fica sem ele.
        var ordemRecebidaPeloAcceptor = Assert.Single(_loggedOnOrderGenerator.FixAcceptor.ReceivedOrders);
        Assert.Equal($"00-{envioDaOrdem.TraceId}-{envioDaOrdem.SpanId}-01", ordemRecebidaPeloAcceptor.GetString(FixOrderTraceProvider.TraceParentTag));
        var linhasDoStdout = stdoutCapturado.ToString().Split('\n');
        Assert.Single(linhasDoStdout, linhaDoStdout => linhaDoStdout.StartsWith("<outgoing> ") && linhaDoStdout.Contains("|35=D|") && linhaDoStdout.Contains("|5100=***|"));
        Assert.DoesNotContain(linhasDoStdout, linhaDoStdout => linhaDoStdout.Contains(envioDaOrdem.TraceId.ToHexString()));
        Assert.DoesNotContain(linhasDoStdout, linhaDoStdout => linhaDoStdout.Contains(envioDaOrdem.SpanId.ToHexString()));
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
