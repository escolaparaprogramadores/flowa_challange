import { useCallback, useEffect, useRef, useState } from 'react';
import { Boleta } from './Boleta';
import { formatarQuantidade, formatarReais } from './lib/validacaoDaOrdem';
import {
  enviarOrdem,
  lerExposicoes,
  type ExposicaoDoSimbolo,
  type OrdemParaEnviar,
  type RespostaDaOrdem,
} from './ordensService';

type EstadoDasExposicoes =
  | { situacao: 'carregando' }
  | { situacao: 'erro'; mensagemDeErro: string }
  | { situacao: 'pronto'; exposicoesPorSimbolo: ExposicaoDoSimbolo[] };

export function PaginaDaBoletaEExposicao() {
  const [estadoDasExposicoes, setEstadoDasExposicoes] = useState<EstadoDasExposicoes>({ situacao: 'carregando' });
  const [enviandoOrdem, setEnviandoOrdem] = useState(false);
  const [respostaDaUltimaOrdem, setRespostaDaUltimaOrdem] = useState<RespostaDaOrdem>();
  const numeroDaUltimaLeituraDaExposicao = useRef(0);

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

  useEffect(() => {
    void atualizarExposicoes();
  }, [atualizarExposicoes]);

  async function aoEnviarOrdem(ordem: OrdemParaEnviar) {
    setEnviandoOrdem(true);
    try {
      setRespostaDaUltimaOrdem(await enviarOrdem(ordem));
    } finally {
      setEnviandoOrdem(false);
    }
    // A exposição vem sempre do servidor: a tela não soma nada por conta própria.
    await atualizarExposicoes();
  }

  return (
    <div className="pagina">
      <header className="topo">
        <LogoBase />
        <div>
          <h1 className="titulo">Boleta de ordens</h1>
          <p className="apoio">
            Envie ordens de compra e venda de PETR4, VALE3 e VIIA4 e acompanhe a exposição de cada ativo até o limite de
            R$ 100.000.000,00.
          </p>
        </div>
      </header>

      <main className="grade">
        <PainelDeExposicao estadoDasExposicoes={estadoDasExposicoes} />
        <PainelDeResposta enviandoOrdem={enviandoOrdem} respostaDaUltimaOrdem={respostaDaUltimaOrdem} />
        <Boleta enviandoOrdem={enviandoOrdem} aoEnviarOrdem={aoEnviarOrdem} />
      </main>
    </div>
  );
}

function LogoBase() {
  return (
    <span className="logo" role="img" aria-label="Base investimentos">
      <span className="logo-base" aria-hidden="true">base</span>
      <span className="logo-complemento" aria-hidden="true">investimentos</span>
    </span>
  );
}

function PainelDeExposicao({ estadoDasExposicoes }: { estadoDasExposicoes: EstadoDasExposicoes }) {
  return (
    <section className="painel exposicao" aria-labelledby="titulo-exposicao" aria-busy={estadoDasExposicoes.situacao === 'carregando'}>
      <h2 className="painel-cabeca" id="titulo-exposicao">Exposição por ativo</h2>
      {estadoDasExposicoes.situacao === 'carregando' && <p className="exposicao-aviso">Carregando a exposição…</p>}
      {estadoDasExposicoes.situacao === 'erro' && (
        <p className="exposicao-aviso erro" role="alert">{estadoDasExposicoes.mensagemDeErro}</p>
      )}
      {estadoDasExposicoes.situacao === 'pronto' && (
        <ul className="exposicao-lista">
          {estadoDasExposicoes.exposicoesPorSimbolo.map((exposicaoDoSimbolo) => (
            <li className="exposicao-item" key={exposicaoDoSimbolo.simbolo} data-testid={`exposicao-${exposicaoDoSimbolo.simbolo}`}>
              <span className="exposicao-simbolo">{exposicaoDoSimbolo.simbolo}</span>
              <dl className="exposicao-dados">
                <dt>Exposição atual</dt>
                <dd className="num" data-testid="exposicao-atual">{formatarReais(exposicaoDoSimbolo.exposicao)}</dd>
                <dt>Falta até o limite</dt>
                <dd className="num" data-testid="exposicao-restante">{formatarReais(exposicaoDoSimbolo.restanteAteOLimite)}</dd>
              </dl>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

const ROTULO_DA_SITUACAO_DA_ORDEM = {
  aceita: 'Aceita',
  rejeitada: 'Rejeitada',
  invalida: 'Não enviada',
  'falha-de-comunicacao': 'Erro de comunicação',
} as const;

type PropsDoPainelDeResposta = { enviandoOrdem: boolean; respostaDaUltimaOrdem?: RespostaDaOrdem };

function PainelDeResposta({ enviandoOrdem, respostaDaUltimaOrdem }: PropsDoPainelDeResposta) {
  const painelDeResposta = useRef<HTMLElement>(null);

  // No celular a resposta fica abaixo da boleta; traz o painel para a vista quando ela chega.
  useEffect(() => {
    if (respostaDaUltimaOrdem) painelDeResposta.current?.scrollIntoView({ block: 'nearest' });
  }, [respostaDaUltimaOrdem]);

  return (
    <section ref={painelDeResposta} className="cartao resposta" aria-labelledby="titulo-resposta" aria-live="polite">
      <h2 className="cartao-titulo" id="titulo-resposta">Resposta da ordem</h2>
      {enviandoOrdem && <span className="status status-enviando">Enviando…</span>}
      {!enviandoOrdem && !respostaDaUltimaOrdem && (
        <p className="resposta-vazia">Nenhuma ordem enviada ainda. Preencha a boleta e envie para ver a resposta aqui.</p>
      )}
      {!enviandoOrdem && respostaDaUltimaOrdem && <DetalheDaResposta respostaDaOrdem={respostaDaUltimaOrdem} />}
    </section>
  );
}

function DetalheDaResposta({ respostaDaOrdem }: { respostaDaOrdem: RespostaDaOrdem }) {
  const classeCssDaSituacao =
    respostaDaOrdem.situacao === 'aceita' ? 'status-aceita' : respostaDaOrdem.situacao === 'rejeitada' ? 'status-rejeitada' : 'status-erro';
  return (
    <>
      <span className={`status ${classeCssDaSituacao}`} data-testid="status-da-ordem">{ROTULO_DA_SITUACAO_DA_ORDEM[respostaDaOrdem.situacao]}</span>
      <p className="resposta-motivo" data-testid="mensagem-da-ordem">{respostaDaOrdem.mensagemDoServidor}</p>
      {respostaDaOrdem.situacao === 'invalida' && respostaDaOrdem.errosDeCampo.length > 0 && (
        <ul className="resposta-motivo" data-testid="erros-de-campo-da-ordem">
          {respostaDaOrdem.errosDeCampo.map((mensagemDoErroDeCampo) => (
            <li key={mensagemDoErroDeCampo}>{mensagemDoErroDeCampo}</li>
          ))}
        </ul>
      )}
      {(respostaDaOrdem.situacao === 'aceita' || respostaDaOrdem.situacao === 'rejeitada') && (
        <dl className="resposta-dados">
          <div className="resposta-celula"><dt>Ativo</dt><dd>{respostaDaOrdem.simbolo}</dd></div>
          <div className="resposta-celula"><dt>Lado</dt><dd>{respostaDaOrdem.lado}</dd></div>
          <div className="resposta-celula"><dt>Quantidade</dt><dd className="num">{formatarQuantidade(respostaDaOrdem.quantidade)}</dd></div>
          <div className="resposta-celula"><dt>Preço</dt><dd className="num">{formatarReais(respostaDaOrdem.precoEmReais)}</dd></div>
          <div className="resposta-celula"><dt>Número da ordem</dt><dd className="num">{respostaDaOrdem.orderId}</dd></div>
          <div className="resposta-celula"><dt>Identificador do envio</dt><dd className="num">{respostaDaOrdem.clOrdId}</dd></div>
        </dl>
      )}
    </>
  );
}
