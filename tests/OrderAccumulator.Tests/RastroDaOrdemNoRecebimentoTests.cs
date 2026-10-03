using System.Collections.Concurrent;
using System.Diagnostics;
using Flowa.Shared.Fix;
using QuickFix.Fields;
using QuickFix.FIX44;

namespace OrderAccumulator.Tests;

// CA-O5 da onda 3: o contexto do rastro chega na tag 5100 e nunca muda o destino da ordem.
// Os testes FIX desta coleção rodam um de cada vez, então cada um vê só o recebimento da sua ordem.
[Collection(OrderAccumulatorPostgresCollection.Name)]
public sealed class RastroDaOrdemNoRecebimentoTests(OrderAccumulatorPostgresFixture orderAccumulatorDatabase) : IAsyncLifetime
{
    public Task InitializeAsync() => orderAccumulatorDatabase.ResetOrdersAndExposuresAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Ordem_com_traceparent_na_tag_5100_e_aceita_no_mesmo_rastro_do_envio()
    {
        using var spansDoRastro = new SpansDoRastroDaOrdemCapturados();
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();
        using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort);
        // O envio sai do mesmo helper que o OrderGenerator usa (FixOrderClient).
        using var envioDaOrdem = RastroDaOrdemFix.IniciarEnvioDaOrdem();
        Assert.NotNull(envioDaOrdem);
        var ordemComRastro = FixTestInitiator.NewOrder("rastro-com-5100", "PETR4", '1', 100, 10.50m);
        ordemComRastro.SetField(new StringField(RastroDaOrdemFix.TagTraceParent, RastroDaOrdemFix.TraceParentDoEnvio(envioDaOrdem)!));

        var relatorioDaOrdem = await fixTestInitiator.SendExpectingExecutionReportAsync(ordemComRastro);

        Assert.Equal(ExecType.NEW, relatorioDaOrdem.ExecType.Value);
        Assert.Equal(1, await orderAccumulatorDatabase.CountStoredOrdersAsync("rastro-com-5100"));
        var recebimentoDaOrdem = Assert.Single(spansDoRastro.RecebimentosDaOrdem);
        Assert.Equal(envioDaOrdem.TraceId, recebimentoDaOrdem.TraceId);
        Assert.Equal(envioDaOrdem.SpanId, recebimentoDaOrdem.ParentSpanId);
        Assert.Equal(ActivityKind.Consumer, recebimentoDaOrdem.Kind);
        // Decisão 17: nenhuma etiqueta no span, e o ClOrdID em particular nunca.
        Assert.Empty(recebimentoDaOrdem.TagObjects);
    }

    [Fact]
    public async Task Log_da_sessao_FIX_mostra_a_tag_5100_sem_o_trace_id()
    {
        // Lê o stdout de verdade, como o FixAcceptorTests: é o que o docker compose logs e o CloudWatch recebem.
        var stdoutCapturado = new StringWriter();
        var stdoutOriginal = Console.Out;
        Console.SetOut(TextWriter.Synchronized(stdoutCapturado));
        string traceIdDoEnvio;
        string spanIdDoEnvio;
        try
        {
            using var spansDoRastro = new SpansDoRastroDaOrdemCapturados();
            await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();
            using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort);
            using var envioDaOrdem = RastroDaOrdemFix.IniciarEnvioDaOrdem();
            Assert.NotNull(envioDaOrdem);
            traceIdDoEnvio = envioDaOrdem.TraceId.ToHexString();
            spanIdDoEnvio = envioDaOrdem.SpanId.ToHexString();
            var ordemComRastro = FixTestInitiator.NewOrder("log-sem-trace-id", "PETR4", '1', 100, 10.50m);
            ordemComRastro.SetField(new StringField(RastroDaOrdemFix.TagTraceParent, RastroDaOrdemFix.TraceParentDoEnvio(envioDaOrdem)!));

            var relatorioDaOrdem = await fixTestInitiator.SendExpectingExecutionReportAsync(ordemComRastro);

            Assert.Equal(ExecType.NEW, relatorioDaOrdem.ExecType.Value);
            Assert.Equal(envioDaOrdem.TraceId, Assert.Single(spansDoRastro.RecebimentosDaOrdem).TraceId);
        }
        finally
        {
            Console.SetOut(stdoutOriginal);
        }

        var linhasDoStdout = stdoutCapturado.ToString().Split(Environment.NewLine);
        Assert.Single(linhasDoStdout, linhaDoStdout => linhaDoStdout.Contains("\u000135=D\u0001")
            && linhaDoStdout.Contains("\u000111=log-sem-trace-id\u0001") && linhaDoStdout.Contains("\u00015100=***\u0001"));
        Assert.DoesNotContain(linhasDoStdout, linhaDoStdout => linhaDoStdout.Contains(traceIdDoEnvio));
        Assert.DoesNotContain(linhasDoStdout, linhaDoStdout => linhaDoStdout.Contains(spanIdDoEnvio));
    }

    [Fact]
    public async Task Ordem_sem_a_tag_5100_e_aceita_e_abre_um_rastro_novo()
    {
        using var spansDoRastro = new SpansDoRastroDaOrdemCapturados();
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();
        using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort);
        var ordemSemRastro = FixTestInitiator.NewOrder("rastro-sem-5100", "VALE3", '2', 200, 61.37m);

        var relatorioDaOrdem = await fixTestInitiator.SendExpectingExecutionReportAsync(ordemSemRastro);

        Assert.Equal(ExecType.NEW, relatorioDaOrdem.ExecType.Value);
        Assert.Equal(1, await orderAccumulatorDatabase.CountStoredOrdersAsync("rastro-sem-5100"));
        var recebimentoDaOrdem = Assert.Single(spansDoRastro.RecebimentosDaOrdem);
        Assert.Equal(default, recebimentoDaOrdem.ParentSpanId);
        Assert.Null(recebimentoDaOrdem.ParentId);
        Assert.NotEqual(default, recebimentoDaOrdem.TraceId);
    }

    [Theory]
    [InlineData("nao-e-um-traceparent")]
    [InlineData("00-00000000000000000000000000000000-0000000000000000-01")]
    [InlineData("00-0af7651916cd43dd8448eb211c80319c-b7ad6b71692033")]
    public async Task Ordem_com_tag_5100_malformada_e_aceita_e_abre_um_rastro_novo(string traceParentMalformado)
    {
        using var spansDoRastro = new SpansDoRastroDaOrdemCapturados();
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();
        using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort);
        var ordemComRastroMalformado = FixTestInitiator.NewOrder("rastro-malformado", "VIIA4", '1', 10, 3.21m);
        ordemComRastroMalformado.SetField(new StringField(RastroDaOrdemFix.TagTraceParent, traceParentMalformado));

        var relatorioDaOrdem = await fixTestInitiator.SendExpectingExecutionReportAsync(ordemComRastroMalformado);

        Assert.Equal(ExecType.NEW, relatorioDaOrdem.ExecType.Value);
        Assert.Equal(1, await orderAccumulatorDatabase.CountStoredOrdersAsync("rastro-malformado"));
        var recebimentoDaOrdem = Assert.Single(spansDoRastro.RecebimentosDaOrdem);
        Assert.Equal(default, recebimentoDaOrdem.ParentSpanId);
        Assert.NotEqual(default, recebimentoDaOrdem.TraceId);
    }

    [Fact]
    public async Task Campo_de_usuario_fora_do_dicionario_continua_recusado_pela_sessao_FIX()
    {
        await using var orderAccumulatorTestApp = new OrderAccumulatorFixTestHost(orderAccumulatorDatabase.OrderDatabaseConnectionString).StartWithFixAcceptor();
        using var fixTestInitiator = await FixTestInitiator.LogOnToAcceptorAsync(orderAccumulatorTestApp.FixAcceptorPort);
        var ordemComCampoDesconhecido = FixTestInitiator.NewOrder("campo-5101", "PETR4", '1', 100, 10.50m);
        ordemComCampoDesconhecido.SetField(new StringField(5101, "fora-do-dicionario"));

        var recusaDaSessao = await fixTestInitiator.SendExpectingSessionRejectAsync(ordemComCampoDesconhecido);

        Assert.Equal(5101, recusaDaSessao.RefTagID.Value);
        // Tag que o dicionário nem define: o QuickFIX recusa como número de tag inválido (373=0).
        Assert.Equal(SessionRejectReason.INVALID_TAG_NUMBER, recusaDaSessao.SessionRejectReason.Value);
        Assert.Equal(0, await orderAccumulatorDatabase.CountStoredOrdersAsync("campo-5101"));
    }

    // Ouve só a fonte do rastro da ordem e guarda cada recebimento no início, antes da resposta sair.
    private sealed class SpansDoRastroDaOrdemCapturados : IDisposable
    {
        private readonly ConcurrentQueue<Activity> recebimentosDaOrdem = new();
        private readonly ActivityListener ouvinteDoRastroDaOrdem;

        public SpansDoRastroDaOrdemCapturados()
        {
            ouvinteDoRastroDaOrdem = new ActivityListener
            {
                ShouldListenTo = fonteDoRastro => fonteDoRastro.Name == RastroDaOrdemFix.NomeDaFonteDoRastro,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStarted = spanIniciado =>
                {
                    if (spanIniciado.OperationName == "fix.recebimento_da_ordem")
                        recebimentosDaOrdem.Enqueue(spanIniciado);
                }
            };
            ActivitySource.AddActivityListener(ouvinteDoRastroDaOrdem);
        }

        public IReadOnlyList<Activity> RecebimentosDaOrdem => recebimentosDaOrdem.ToList();

        public void Dispose() => ouvinteDoRastroDaOrdem.Dispose();
    }
}
