import { useCallback, useEffect, useRef, useState } from 'react';
import { Boleta } from './Boleta';
import { CompraVenda, type EstadoDaListaDeOrdens, type FalhaNoEnvioDaOrdem } from './CompraVenda';
import { ExposicaoPorAtivo, type EstadoDasExposicoes } from './ExposicaoPorAtivo';
import { Topo } from './Topo';
import './boleta.css';
import './compra-venda.css';
import { enviarOrdem, lerExposicoes, listarOrdens, type OrdemParaEnviar } from './ordensService';

export function PaginaDaBoletaEExposicao() {
  const [estadoDasExposicoes, setEstadoDasExposicoes] = useState<EstadoDasExposicoes>({ situacao: 'carregando' });
  const [estadoDaListaDeOrdens, setEstadoDaListaDeOrdens] = useState<EstadoDaListaDeOrdens>({ situacao: 'carregando' });
  const [enviandoOrdem, setEnviandoOrdem] = useState(false);
  const [falhaNoUltimoEnvio, setFalhaNoUltimoEnvio] = useState<FalhaNoEnvioDaOrdem>();
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
  const atualizarListaDeOrdens = useCallback(async () => {
    const numeroDestaLeitura = ++numeroDaUltimaLeituraDaListaDeOrdens.current;
    const estadoDaListaDeOrdensLido: EstadoDaListaDeOrdens = await listarOrdens(1).then(
      (paginaDeOrdens) => ({ situacao: 'pronto', paginaDeOrdens }),
      (falhaNaLeitura: Error) => ({ situacao: 'erro', mensagemDeErro: falhaNaLeitura.message }),
    );
    if (numeroDestaLeitura === numeroDaUltimaLeituraDaListaDeOrdens.current) setEstadoDaListaDeOrdens(estadoDaListaDeOrdensLido);
  }, []);

  useEffect(() => {
    void atualizarExposicoes();
    void atualizarListaDeOrdens();
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
    await Promise.all([atualizarExposicoes(), atualizarListaDeOrdens()]);
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
        <CompraVenda estadoDaListaDeOrdens={estadoDaListaDeOrdens} enviandoOrdem={enviandoOrdem} falhaNoUltimoEnvio={falhaNoUltimoEnvio} />
        <Boleta enviandoOrdem={enviandoOrdem} aoEnviarOrdem={aoEnviarOrdem} />
      </main>
    </div>
  );
}
