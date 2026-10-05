using Flowa.Shared.Fix;

namespace Flowa.Shared.Tests;

// Decisão 17: o trace id que viaja na tag 5100 não vai para o log da sessão FIX.
public class RastroDaOrdemFixTests
{
    private const string TraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";

    [Fact]
    public void Linha_com_a_5100_no_meio_mantem_a_tag_e_perde_so_o_valor()
    {
        var linhaDoLog = $"8=FIX.4.4\u000135=D\u000111=abc\u00015100={TraceParent}\u000110=128\u0001";

        var linhaSemTraceParent = FixOrderTraceProvider.HideTraceParentInLog(linhaDoLog);

        Assert.Equal("8=FIX.4.4\u000135=D\u000111=abc\u00015100=***\u000110=128\u0001", linhaSemTraceParent);
    }

    [Fact]
    public void Linha_que_comeca_pela_5100_tambem_perde_o_valor()
    {
        Assert.Equal("5100=***\u000110=128", FixOrderTraceProvider.HideTraceParentInLog($"5100={TraceParent}\u000110=128"));
    }

    [Fact]
    public void Outra_tag_terminada_em_5100_e_valores_com_5100_ficam_iguais()
    {
        var linhaDoLog = "8=FIX.4.4\u000115100=x\u000158=5100=y\u000110=128\u0001";

        Assert.Equal(linhaDoLog, FixOrderTraceProvider.HideTraceParentInLog(linhaDoLog));
    }

    [Fact]
    public void Linha_sem_a_5100_volta_a_mesma_instancia()
    {
        var linhaDoLog = "8=FIX.4.4\u000135=A\u000110=213\u0001";

        Assert.Same(linhaDoLog, FixOrderTraceProvider.HideTraceParentInLog(linhaDoLog));
    }
}
