import { useCallback, useEffect, useRef, useState } from 'react';
import { Boleta } from './Boleta';
import { CompraVenda, type EstadoDaListaDeOrdens, type FalhaNoEnvioDaOrdem } from './CompraVenda';
import { ExposicaoPorAtivo, type EstadoDasExposicoes } from './ExposicaoPorAtivo';
import { Paginacao } from './Paginacao';
import { Topo } from './Topo';
import './boleta.css';
import './compra-venda.css';
import { contarPaginasDaLista } from './lib/paginasVisiveis';
import { enviarOrdem, lerExposicoes, listarOrdens, type OrdemParaEnviar } from './ordensService';

// Outra aba pode ter apagado ordens: a página além da última volta vazia com o total real,
// e aí a tela pede a última página que ainda existe em vez de mostrar a lista como vazia.
async function lerPaginaDaListaDeOrdens(paginaPedida: number): Promise<EstadoDaListaDeOrdens> {
  try {
    const paginaDeOrdens = await listarOrdens(paginaPedida);
    const ultimaPaginaComOrdens = contarPaginasDaLista(paginaDeOrdens.totalDeOrdens);
    if (paginaDeOrdens.ordens.length === 0 && paginaPedida > ultimaPaginaComOrdens && ultimaPaginaComOrdens >= 1) {
      return await lerPaginaDaListaDeOrdens(ultimaPaginaComOrdens);
    }
    return { situacao: 'pronto', paginaDeOrdens };
  } catch (falhaNaLeitura) {
    return { situacao: 'erro', mensagemDeErro: (falhaNaLeitura as Error).message };
  }
}

export function PaginaDaBoletaEExposicao() {
  const [estadoDasExposicoes, setEstadoDasExposicoes] = useState<EstadoDasExposicoes>({ situacao: 'carregando' });
  const [estadoDaListaDeOrdens, setEstadoDaListaDeOrdens] = useState<EstadoDaListaDeOrdens>({ situacao: 'carregando' });
  const [enviandoOrdem, setEnviandoOrdem] = useState(false);
  const [falhaNoUltimoEnvio, setFalhaNoUltimoEnvio] = useState<FalhaNoEnvioDaOrdem>();
  const [paginaDaListaSendoCarregada, setPaginaDaListaSendoCarregada] = useState<number>();
  const numeroDaUltimaLeituraDaExposicao = useRef(0);
  const numeroDaUltimaLeituraDaListaDeOrdens = useRef(0);

  // Duas leituras podem estar abertas ao mesmo tempo; só a última pedida pode mudar o painel,
  // senão uma resposta antiga e lenta apagaria a exposição já atualizada depois de um envio.
  const atualizarExposicoes = useCallback(async () => {
    const numeroDestaLeitura = ++numeroDaUltimaLeituraDaExposicao.current;
    const estadoDasExposicoesLido: EstadoDasExposicoes = await lerExposicoes().then(
      (exposicoesPorSimbolo) => ({ situacao: 'pronto', exposicoesPorSimbolo }),
      (falhaNaLeitura: Error) => ({ situacao: 'erro', mensagemDeErro: falhaNaLeitura.message }),
    );
    if (numeroDestaLeitura === numeroDaUltimaLeituraDaExposicao.current) setEstadoDasExposicoes(estadoDasExposicoesLido);
  }, []);

  // Mesma guarda da exposição: a lista lida antes do envio não pode cobrir a lida depois dele.
  // Também vale entre cliques rápidos na paginação: só a última página pedida aparece.
  const atualizarListaDeOrdens = useCallback(async (paginaPedida: number) => {
    const numeroDestaLeitura = ++numeroDaUltimaLeituraDaListaDeOrdens.current;
    setPaginaDaListaSendoCarregada(paginaPedida);
    const estadoDaListaDeOrdensLido = await lerPaginaDaListaDeOrdens(paginaPedida);
    if (numeroDestaLeitura !== numeroDaUltimaLeituraDaListaDeOrdens.current) return;
    setEstadoDaListaDeOrdens(estadoDaListaDeOrdensLido);
    setPaginaDaListaSendoCarregada(undefined);
  }, []);

  useEffect(() => {
    void atualizarExposicoes();
    void atualizarListaDeOrdens(1);
  }, [atualizarExposicoes, atualizarListaDeOrdens]);

  async function aoEnviarOrdem(ordemParaEnviar: OrdemParaEnviar) {
    setEnviandoOrdem(true);
    setFalhaNoUltimoEnvio(undefined);
    try {
      const respostaDaOrdem = await enviarOrdem(ordemParaEnviar);
      if (respostaDaOrdem.situacao === 'invalida' || respostaDaOrdem.situacao === 'falha-de-comunicacao') setFalhaNoUltimoEnvio(respostaDaOrdem);
    } finally {
      setEnviandoOrdem(false);
    }
    // Exposição e lista vêm sempre do servidor: a tela não soma nem monta linha por conta própria.
    // A ordem nova é a mais recente, então a lista volta para a página 1, onde ela aparece no topo.
    await Promise.all([atualizarExposicoes(), atualizarListaDeOrdens(1)]);
  }

  return (
    <div className="pagina">
      <Topo />
      <div className="cabecalho-da-pagina">
        <h1 className="titulo">Boleta de ordens</h1>
        <p className="apoio">
          Envie ordens de compra e venda de PETR4, VALE3 e VIIA4 e acompanhe a exposição de cada ativo até o limite de
          R$ 100.000.000,00.
        </p>
      </div>

      <main className="grade">
        <ExposicaoPorAtivo estadoDasExposicoes={estadoDasExposicoes} />
        <CompraVenda
          estadoDaListaDeOrdens={estadoDaListaDeOrdens}
          enviandoOrdem={enviandoOrdem}
          falhaNoUltimoEnvio={falhaNoUltimoEnvio}
          rodape={
            estadoDaListaDeOrdens.situacao === 'pronto' && (
              <Paginacao
                paginaAtual={estadoDaListaDeOrdens.paginaDeOrdens.pagina}
                totalDeOrdens={estadoDaListaDeOrdens.paginaDeOrdens.totalDeOrdens}
                quantidadeDeOrdensNaPagina={estadoDaListaDeOrdens.paginaDeOrdens.ordens.length}
                paginaSendoCarregada={paginaDaListaSendoCarregada}
                aoEscolherPagina={(paginaEscolhida) => void atualizarListaDeOrdens(paginaEscolhida)}
              />
            )
          }
        />
        <Boleta enviandoOrdem={enviandoOrdem} aoEnviarOrdem={aoEnviarOrdem} />
      </main>
    </div>
  );
}
