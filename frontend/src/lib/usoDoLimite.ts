export const LIMITE_DE_EXPOSICAO_POR_ATIVO_EM_REAIS = 100_000_000;

// 1 centésimo de ponto percentual do limite de R$ 100.000.000,00 vale R$ 10.000,00 = 1.000.000 centavos.
const CENTAVOS_POR_CENTESIMO_DE_PORCENTAGEM = 1_000_000;
const CENTESIMOS_DE_PORCENTAGEM_DO_ALERTA = 9_000;

const formatadorDaPorcentagem = new Intl.NumberFormat('pt-BR', { maximumFractionDigits: 2 });

export type UsoDoLimite = {
  porcentagemMostrada: string;
  larguraDaBarraEmPorcentagem: number;
  estaPertoDoLimite: boolean;
};

// Venda que deixa a exposição negativa também consome o limite: a conta usa o valor sem sinal.
// A porcentagem é cortada em 2 casas, nunca arredondada para cima, e a conta corre em centavos
// inteiros para o ponto flutuante não transformar 89,999% em 90%.
export function calcularUsoDoLimite(exposicaoEmReais: number): UsoDoLimite {
  const exposicaoSemSinalEmCentavos = Math.round(Math.abs(exposicaoEmReais) * 100);
  const centesimosDePorcentagem =
    (exposicaoSemSinalEmCentavos - (exposicaoSemSinalEmCentavos % CENTAVOS_POR_CENTESIMO_DE_PORCENTAGEM)) /
    CENTAVOS_POR_CENTESIMO_DE_PORCENTAGEM;
  const porcentagemCortada = centesimosDePorcentagem / 100;
  const usoAbaixoDoMenorValorMostrado = exposicaoSemSinalEmCentavos > 0 && centesimosDePorcentagem === 0;

  return {
    porcentagemMostrada: usoAbaixoDoMenorValorMostrado ? '< 0,01%' : `${formatadorDaPorcentagem.format(porcentagemCortada)}%`,
    larguraDaBarraEmPorcentagem: Math.min(porcentagemCortada, 100),
    estaPertoDoLimite: centesimosDePorcentagem >= CENTESIMOS_DE_PORCENTAGEM_DO_ALERTA,
  };
}
