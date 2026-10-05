import type { LadoDaOrdem, SimboloDaBoleta } from './lib/validacaoDaOrdem';

// Rotas e formatos: docs/contracts/contracts.md, seção 1.
export const ROTA_DE_CRIACAO_DE_ORDEM = '/api/orders';
export const ROTA_DAS_EXPOSICOES = '/api/exposures';
export const ROTA_DAS_ORDENS = '/api/orders';

// O OrderGenerator já responde 503 depois de 5 s sem resposta do OrderAccumulator.
// Este prazo é a rede de segurança para a tela nunca ficar presa se o próprio Generator calar.
export const PRAZO_MAXIMO_DE_ESPERA_DA_TELA_EM_MS = 6_000;

// Sem resposta confirmada, a tela não sabe se a ordem chegou: diz só o que é certo, sem nome de serviço interno.
export const MENSAGEM_DE_ORDEM_NAO_CONFIRMADA = 'A ordem não foi confirmada: o servidor de ordens não respondeu. Tente de novo em instantes.';
export const MENSAGEM_DE_ERRO_INESPERADO_NO_SERVIDOR = 'A ordem não foi confirmada: o servidor de ordens teve um erro inesperado. Tente de novo em instantes.';
export const MENSAGEM_DE_EXPOSICAO_INDISPONIVEL = 'Não foi possível ler a exposição agora. Tente de novo em instantes.';
export const MENSAGEM_DE_LISTA_DE_ORDENS_INDISPONIVEL = 'Não foi possível ler as ordens agora. Tente de novo em instantes.';
export const MENSAGEM_DE_ORDENS_NAO_APAGADAS = 'Não foi possível apagar as ordens agora. Tente de novo em instantes.';

export type OrdemParaEnviar = {
  simbolo: SimboloDaBoleta;
  lado: LadoDaOrdem;
  quantidade: number;
  precoEmCentavos: number;
};

export type RespostaDaOrdem =
  | {
      situacao: 'aceita' | 'rejeitada';
      mensagemDoServidor: string;
      clOrdId: string;
      orderId: string;
      simbolo: string;
      lado: LadoDaOrdem;
      quantidade: number;
      precoEmReais: number;
    }
  | { situacao: 'invalida'; mensagemDoServidor: string; errosDeCampo: string[] }
  | { situacao: 'falha-de-comunicacao'; mensagemDoServidor: string };

export type ExposicaoDoSimbolo = { simbolo: string; exposicao: number; restanteAteOLimite: number };

type CorpoDaRespostaDaOrdem = {
  status?: string; message?: string; clOrdId?: string; orderId?: string; symbol?: string; side?: string;
  quantity?: number; price?: number; errors?: Array<{ field: string; message: string }>;
};

type CorpoDasExposicoes = { exposures?: Array<{ symbol: string; exposure: number; remaining: number }>; message?: string };

export type OrdemDaLista = {
  recebidaEm: string;
  situacao: 'aceita' | 'rejeitada';
  simbolo: string | null;
  lado: LadoDaOrdem | null;
  quantidade: number;
  precoEmReais: number;
  orderId: string;
  clOrdId: string;
};

export type PaginaDeOrdens = { pagina: number; totalDeOrdens: number; ordens: OrdemDaLista[] };

type OrdemGravadaNoServidor = {
  receivedAt: string; status: string; symbol: string | null; side: string | null;
  quantity: number; price: number; orderId: string; clOrdId: string;
};

type CorpoDaPaginaDeOrdens = { page?: number; total?: number; orders?: OrdemGravadaNoServidor[] };

// O prazo vale até o corpo terminar de chegar: um servidor que manda os cabeçalhos e trava
// no corpo também é abandonado. Corpo vazio, HTML ou cortado vira "sem corpo".
async function chamarApiDoOrderGeneratorComPrazo<CorpoEsperado>(rotaDaApi: string, opcoesDaRequisicao: RequestInit = {}) {
  const cancelamentoPorPrazo = new AbortController();
  const temporizadorDoPrazo = setTimeout(() => cancelamentoPorPrazo.abort(), PRAZO_MAXIMO_DE_ESPERA_DA_TELA_EM_MS);
  try {
    const respostaHttp = await fetch(rotaDaApi, { ...opcoesDaRequisicao, signal: cancelamentoPorPrazo.signal });
    const corpoDaResposta = (await respostaHttp.json().catch(() => undefined)) as CorpoEsperado | undefined;
    return { respostaHttp, corpoDaResposta };
  } finally {
    clearTimeout(temporizadorDoPrazo);
  }
}

export async function enviarOrdem(ordemParaEnviar: OrdemParaEnviar): Promise<RespostaDaOrdem> {
  let respostaDaCriacaoDaOrdem: Awaited<ReturnType<typeof chamarApiDoOrderGeneratorComPrazo<CorpoDaRespostaDaOrdem>>>;
  try {
    respostaDaCriacaoDaOrdem = await chamarApiDoOrderGeneratorComPrazo<CorpoDaRespostaDaOrdem>(ROTA_DE_CRIACAO_DE_ORDEM, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        symbol: ordemParaEnviar.simbolo,
        side: ordemParaEnviar.lado === 'Compra' ? 'buy' : 'sell',
        quantity: ordemParaEnviar.quantidade,
        price: ordemParaEnviar.precoEmCentavos / 100,
      }),
    });
  } catch {
    return { situacao: 'falha-de-comunicacao', mensagemDoServidor: MENSAGEM_DE_ORDEM_NAO_CONFIRMADA };
  }

  const { respostaHttp, corpoDaResposta } = respostaDaCriacaoDaOrdem;
  if (respostaHttp.ok && (corpoDaResposta?.status === 'accepted' || corpoDaResposta?.status === 'rejected')) {
    return {
      situacao: corpoDaResposta.status === 'accepted' ? 'aceita' : 'rejeitada',
      mensagemDoServidor: corpoDaResposta.message ?? '',
      clOrdId: corpoDaResposta.clOrdId ?? '',
      orderId: corpoDaResposta.orderId ?? '',
      simbolo: corpoDaResposta.symbol ?? ordemParaEnviar.simbolo,
      lado: corpoDaResposta.side === 'sell' ? 'Venda' : 'Compra',
      quantidade: corpoDaResposta.quantity ?? ordemParaEnviar.quantidade,
      precoEmReais: corpoDaResposta.price ?? ordemParaEnviar.precoEmCentavos / 100,
    };
  }
  if (respostaHttp.status === 400 && corpoDaResposta?.status === 'validation_error') {
    return {
      situacao: 'invalida',
      mensagemDoServidor: corpoDaResposta.message ?? 'A ordem tem campos inválidos.',
      errosDeCampo: (corpoDaResposta.errors ?? []).map((erroDeCampo) => erroDeCampo.message),
    };
  }
  // 503 (sem sessão FIX ou sem resposta em 5 s) e corpo que não chegou são falta de resposta; outro status de erro é resposta com erro.
  const servidorRespondeuComErro = respostaHttp.status >= 400 && respostaHttp.status !== 503;
  return {
    situacao: 'falha-de-comunicacao',
    mensagemDoServidor: servidorRespondeuComErro ? MENSAGEM_DE_ERRO_INESPERADO_NO_SERVIDOR : MENSAGEM_DE_ORDEM_NAO_CONFIRMADA,
  };
}

export async function lerExposicoes(): Promise<ExposicaoDoSimbolo[]> {
  const { respostaHttp, corpoDaResposta } = await chamarApiDoOrderGeneratorComPrazo<CorpoDasExposicoes>(ROTA_DAS_EXPOSICOES).catch(() => {
    throw new Error(MENSAGEM_DE_EXPOSICAO_INDISPONIVEL);
  });
  if (!respostaHttp.ok || !corpoDaResposta?.exposures) {
    throw new Error(MENSAGEM_DE_EXPOSICAO_INDISPONIVEL);
  }
  return corpoDaResposta.exposures.map((exposicaoNoServidor) => ({
    simbolo: exposicaoNoServidor.symbol,
    exposicao: exposicaoNoServidor.exposure,
    restanteAteOLimite: exposicaoNoServidor.remaining,
  }));
}

export async function listarOrdens(pagina: number): Promise<PaginaDeOrdens> {
  const { respostaHttp, corpoDaResposta } = await chamarApiDoOrderGeneratorComPrazo<CorpoDaPaginaDeOrdens>(
    `${ROTA_DAS_ORDENS}?page=${pagina}`,
  ).catch(() => {
    throw new Error(MENSAGEM_DE_LISTA_DE_ORDENS_INDISPONIVEL);
  });
  if (!respostaHttp.ok || !Array.isArray(corpoDaResposta?.orders) || typeof corpoDaResposta.total !== 'number') {
    throw new Error(MENSAGEM_DE_LISTA_DE_ORDENS_INDISPONIVEL);
  }
  return {
    pagina: corpoDaResposta.page ?? pagina,
    totalDeOrdens: corpoDaResposta.total,
    ordens: corpoDaResposta.orders.map((ordemGravada) => ({
      recebidaEm: ordemGravada.receivedAt,
      situacao: ordemGravada.status === 'accepted' ? 'aceita' : 'rejeitada',
      simbolo: ordemGravada.symbol,
      lado: ordemGravada.side === 'buy' ? 'Compra' : ordemGravada.side === 'sell' ? 'Venda' : null,
      quantidade: ordemGravada.quantity,
      precoEmReais: ordemGravada.price,
      orderId: ordemGravada.orderId,
      clOrdId: ordemGravada.clOrdId,
    })),
  };
}

export async function apagarTodasAsOrdens(): Promise<void> {
  const { respostaHttp } = await chamarApiDoOrderGeneratorComPrazo(ROTA_DAS_ORDENS, { method: 'DELETE' }).catch(() => {
    throw new Error(MENSAGEM_DE_ORDENS_NAO_APAGADAS);
  });
  if (!respostaHttp.ok) throw new Error(MENSAGEM_DE_ORDENS_NAO_APAGADAS);
}
