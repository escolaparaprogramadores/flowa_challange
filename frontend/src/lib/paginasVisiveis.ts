export const ORDENS_POR_PAGINA = 10;
// O servidor recusa página acima de 1.000 (CA-42); a tela nunca oferece além dela.
export const ULTIMA_PAGINA_PERMITIDA = 1000;
const PAGINAS_MOSTRADAS_SEM_ENCURTAR = 7;

export const RETICENCIAS_DA_PAGINACAO = '…';
export type ItemDaPaginacao = number | typeof RETICENCIAS_DA_PAGINACAO;

export function contarPaginasDaLista(totalDeOrdens: number): number {
  return Math.min(Math.ceil(Math.max(totalDeOrdens, 0) / ORDENS_POR_PAGINA), ULTIMA_PAGINA_PERMITIDA);
}

// Sempre a primeira, a última, a atual e as vizinhas dela; cada trecho que falta vira um "…".
export function listarPaginasVisiveis(paginaAtual: number, totalDePaginas: number): ItemDaPaginacao[] {
  if (totalDePaginas <= 0) return [];
  if (totalDePaginas <= PAGINAS_MOSTRADAS_SEM_ENCURTAR) {
    return Array.from({ length: totalDePaginas }, (_, posicaoDaPagina) => posicaoDaPagina + 1);
  }

  const paginaAtualNoIntervalo = Math.min(Math.max(paginaAtual, 1), totalDePaginas);
  const paginasSempreMostradas = new Set([1, paginaAtualNoIntervalo - 1, paginaAtualNoIntervalo, paginaAtualNoIntervalo + 1, totalDePaginas]);
  const paginasMostradasEmOrdem = [...paginasSempreMostradas]
    .filter((paginaMostrada) => paginaMostrada >= 1 && paginaMostrada <= totalDePaginas)
    .sort((paginaMenor, paginaMaior) => paginaMenor - paginaMaior);

  const itensDaPaginacao: ItemDaPaginacao[] = [];
  paginasMostradasEmOrdem.forEach((paginaMostrada, posicaoDaPagina) => {
    const paginaMostradaAntes = paginasMostradasEmOrdem[posicaoDaPagina - 1];
    if (paginaMostradaAntes !== undefined && paginaMostrada - paginaMostradaAntes > 1) itensDaPaginacao.push(RETICENCIAS_DA_PAGINACAO);
    itensDaPaginacao.push(paginaMostrada);
  });
  return itensDaPaginacao;
}
