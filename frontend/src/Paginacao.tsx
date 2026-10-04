import './paginacao.css';
import { contarPaginasDaLista, listarPaginasVisiveis, ORDENS_POR_PAGINA, RETICENCIAS_DA_PAGINACAO } from './lib/paginasVisiveis';
import { formatarQuantidade } from './lib/validacaoDaOrdem';

type PropsDaPaginacao = {
  paginaAtual: number;
  totalDeOrdens: number;
  quantidadeDeOrdensNaPagina: number;
  aoEscolherPagina: (paginaEscolhida: number) => void;
};

export function Paginacao({ paginaAtual, totalDeOrdens, quantidadeDeOrdensNaPagina, aoEscolherPagina }: PropsDaPaginacao) {
  const totalDePaginas = contarPaginasDaLista(totalDeOrdens);
  if (totalDeOrdens <= ORDENS_POR_PAGINA) return null;

  const primeiraOrdemDaPagina = (paginaAtual - 1) * ORDENS_POR_PAGINA + 1;
  const ultimaOrdemDaPagina = primeiraOrdemDaPagina + quantidadeDeOrdensNaPagina - 1;
  const itensDaPaginacao = listarPaginasVisiveis(paginaAtual, totalDePaginas);

  return (
    <nav className="paginacao" aria-label="Páginas da lista de ordens">
      <p className="paginacao-resumo num" data-testid="resumo-da-paginacao">
        Mostrando {formatarQuantidade(primeiraOrdemDaPagina)}–{formatarQuantidade(ultimaOrdemDaPagina)} de{' '}
        {formatarQuantidade(totalDeOrdens)} ordens
      </p>
      <ul className="paginacao-botoes">
        <li>
          <button
            type="button"
            className="paginacao-botao"
            aria-label="Página anterior"
            disabled={paginaAtual <= 1}
            onClick={() => aoEscolherPagina(paginaAtual - 1)}
          >
            ‹
          </button>
        </li>
        {itensDaPaginacao.map((itemDaPaginacao, posicaoNaFileira) =>
          itemDaPaginacao === RETICENCIAS_DA_PAGINACAO ? (
            <li key={`reticencias-${posicaoNaFileira}`} className="paginacao-reticencias" data-testid="reticencias-da-paginacao">
              {RETICENCIAS_DA_PAGINACAO}
            </li>
          ) : (
            <li key={itemDaPaginacao}>
              <button
                type="button"
                className="paginacao-botao num"
                aria-label={`Página ${itemDaPaginacao}`}
                aria-current={itemDaPaginacao === paginaAtual ? 'page' : undefined}
                onClick={() => aoEscolherPagina(itemDaPaginacao)}
              >
                {itemDaPaginacao}
              </button>
            </li>
          ),
        )}
        <li>
          <button
            type="button"
            className="paginacao-botao"
            aria-label="Próxima página"
            disabled={paginaAtual >= totalDePaginas}
            onClick={() => aoEscolherPagina(paginaAtual + 1)}
          >
            ›
          </button>
        </li>
      </ul>
    </nav>
  );
}
