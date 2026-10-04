import type { ReactNode } from 'react';
import { IconeAlerta, IconeDocumento } from './Icones';
import { formatarInstanteNoHorarioDeBrasilia } from './lib/dataDeBrasilia';
import { formatarQuantidade, formatarReais } from './lib/validacaoDaOrdem';
import type { OrdemDaLista, PaginaDeOrdens, RespostaDaOrdem } from './ordensService';

export type EstadoDaListaDeOrdens =
  | { situacao: 'carregando' }
  | { situacao: 'erro'; mensagemDeErro: string }
  | { situacao: 'pronto'; paginaDeOrdens: PaginaDeOrdens };

export type FalhaNoEnvioDaOrdem = Extract<RespostaDaOrdem, { situacao: 'invalida' | 'falha-de-comunicacao' }>;

type PropsDoCartaoCompraVenda = {
  estadoDaListaDeOrdens: EstadoDaListaDeOrdens;
  enviandoOrdem: boolean;
  falhaNoUltimoEnvio?: FalhaNoEnvioDaOrdem;
  acaoDoCabecalho?: ReactNode;
  rodape?: ReactNode;
};

const ROTULO_DA_FALHA_NO_ENVIO = { invalida: 'Não enviada', 'falha-de-comunicacao': 'Erro de comunicação' } as const;
const ROTULO_DA_SITUACAO_DA_ORDEM_GRAVADA = { aceita: 'Aceita', rejeitada: 'Rejeitada' } as const;

export function CompraVenda({ estadoDaListaDeOrdens, enviandoOrdem, falhaNoUltimoEnvio, acaoDoCabecalho, rodape }: PropsDoCartaoCompraVenda) {
  const temOrdensNaPagina = estadoDaListaDeOrdens.situacao === 'pronto' && estadoDaListaDeOrdens.paginaDeOrdens.ordens.length > 0;
  return (
    <section className="cartao compra-venda" aria-labelledby="titulo-compra-venda" aria-busy={estadoDaListaDeOrdens.situacao === 'carregando'}>
      <div className="compra-venda-cabeca">
        <h2 className="cartao-titulo" id="titulo-compra-venda">Compra/Venda</h2>
        {enviandoOrdem && <span className="selo-da-ordem selo-enviando" role="status" data-testid="selo-enviando">Enviando…</span>}
        {acaoDoCabecalho}
      </div>
      {falhaNoUltimoEnvio && <FaixaDaFalhaNoEnvio falhaNoEnvio={falhaNoUltimoEnvio} />}
      {estadoDaListaDeOrdens.situacao === 'carregando' && <p className="compra-venda-aviso">Carregando as ordens…</p>}
      {estadoDaListaDeOrdens.situacao === 'erro' && (
        <p className="compra-venda-aviso compra-venda-aviso-erro" role="alert">{estadoDaListaDeOrdens.mensagemDeErro}</p>
      )}
      {estadoDaListaDeOrdens.situacao === 'pronto' && !temOrdensNaPagina && <ListaDeOrdensVazia />}
      {temOrdensNaPagina && <TabelaDeOrdens ordens={estadoDaListaDeOrdens.paginaDeOrdens.ordens} />}
      {temOrdensNaPagina && rodape}
    </section>
  );
}

function FaixaDaFalhaNoEnvio({ falhaNoEnvio }: { falhaNoEnvio: FalhaNoEnvioDaOrdem }) {
  return (
    <div className="faixa-da-falha-no-envio" role="alert" data-testid="faixa-da-falha-no-envio">
      <IconeAlerta className="faixa-da-falha-no-envio-icone" />
      <div>
        <p className="faixa-da-falha-no-envio-titulo" data-testid="status-da-ordem">{ROTULO_DA_FALHA_NO_ENVIO[falhaNoEnvio.situacao]}</p>
        <p className="faixa-da-falha-no-envio-mensagem" data-testid="mensagem-da-ordem">{falhaNoEnvio.mensagemDoServidor}</p>
        {falhaNoEnvio.situacao === 'invalida' && falhaNoEnvio.errosDeCampo.length > 0 && (
          <ul className="faixa-da-falha-no-envio-erros" data-testid="erros-de-campo-da-ordem">
            {falhaNoEnvio.errosDeCampo.map((mensagemDoErroDeCampo) => (
              <li key={mensagemDoErroDeCampo}>{mensagemDoErroDeCampo}</li>
            ))}
          </ul>
        )}
      </div>
    </div>
  );
}

function ListaDeOrdensVazia() {
  return (
    <div className="lista-de-ordens-vazia" data-testid="lista-de-ordens-vazia">
      <IconeDocumento className="lista-de-ordens-vazia-icone" />
      <p>Nenhuma ordem enviada ainda. Preencha a boleta e envie para ver a resposta aqui.</p>
    </div>
  );
}

function TabelaDeOrdens({ ordens }: { ordens: OrdemDaLista[] }) {
  return (
    <div className="tabela-de-ordens-moldura">
      <table className="tabela-de-ordens">
        <thead>
          <tr>
            <th scope="col">Data</th>
            <th scope="col">Status</th>
            <th scope="col">Ativo</th>
            <th scope="col">Lado</th>
            <th scope="col">Quantidade</th>
            <th scope="col">Preço</th>
            <th scope="col">Número da ordem</th>
            <th scope="col">Identificador do envio</th>
          </tr>
        </thead>
        <tbody>
          {ordens.map((ordemDaLista) => (
            <LinhaDaOrdem key={`${ordemDaLista.clOrdId}-${ordemDaLista.recebidaEm}`} ordemDaLista={ordemDaLista} />
          ))}
        </tbody>
      </table>
    </div>
  );
}

function LinhaDaOrdem({ ordemDaLista }: { ordemDaLista: OrdemDaLista }) {
  const recebidaNoHorarioDeBrasilia = formatarInstanteNoHorarioDeBrasilia(ordemDaLista.recebidaEm);
  return (
    <tr data-testid="linha-da-ordem">
      <td data-coluna="data">
        <span className="linha-da-ordem-dia num">{recebidaNoHorarioDeBrasilia.diaMesAno}</span>
        <span className="linha-da-ordem-hora num">{recebidaNoHorarioDeBrasilia.horaMinuto}</span>
      </td>
      <td data-coluna="status">
        <span className={`selo-da-ordem selo-${ordemDaLista.situacao}`}>{ROTULO_DA_SITUACAO_DA_ORDEM_GRAVADA[ordemDaLista.situacao]}</span>
      </td>
      <td data-coluna="ativo">{ordemDaLista.simbolo ?? '—'}</td>
      <td data-coluna="lado">{ordemDaLista.lado ?? '—'}</td>
      <td data-coluna="quantidade" className="num">{formatarQuantidade(ordemDaLista.quantidade)}</td>
      <td data-coluna="preco" className="num">{formatarReais(ordemDaLista.precoEmReais)}</td>
      <td data-coluna="numero-da-ordem" className="num linha-da-ordem-codigo">{ordemDaLista.orderId}</td>
      <td data-coluna="identificador-do-envio" className="num linha-da-ordem-codigo">{ordemDaLista.clOrdId}</td>
    </tr>
  );
}
