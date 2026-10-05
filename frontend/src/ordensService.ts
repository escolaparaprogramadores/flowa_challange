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

// "data" of each route (docs/contracts/contracts.md, section 1).
type OrderResponseData = {
  status?: string; clOrdId?: string; orderId?: string; symbol?: string; side?: string; quantity?: number; price?: number;
};

type ExposuresResponseData = { exposures?: Array<{ symbol: string; exposure: number; remaining: number }> };

type StoredOrderResponseData = {
  receivedAt: string; status: string; symbol: string | null; side: string | null;
  quantity: number; price: number; orderId: string; clOrdId: string;
};

type OrdersPageResponseData = { page?: number; total?: number; orders?: StoredOrderResponseData[] };

// Success of the API: a DataMessage. Error: a problem+json (RFC 9457). Both become this one answer here, so the
// rest of the screen never reads a problem+json field.
type ApiAnswer<ResponseData> = { success: boolean; message: string; data: ResponseData | null; errors: string[] };

type ApiProblem = { title?: string; detail?: string; errors?: string[] };

function convertBodyToApiAnswer<ResponseData>(httpResponse: Response, responseBody: unknown): ApiAnswer<ResponseData> | undefined {
  if (typeof responseBody !== 'object' || responseBody === null) return undefined;
  if (httpResponse.headers.get('content-type')?.includes('application/problem+json')) {
    const apiProblem = responseBody as ApiProblem;
    return { success: false, message: apiProblem.detail ?? apiProblem.title ?? '', data: null, errors: apiProblem.errors ?? [] };
  }
  return responseBody as ApiAnswer<ResponseData>;
}

// O prazo vale até o corpo terminar de chegar: um servidor que manda os cabeçalhos e trava
// no corpo também é abandonado. Corpo vazio, HTML ou cortado vira "sem corpo".
async function callOrderGeneratorApiWithDeadline<ResponseData>(apiRoute: string, requestOptions: RequestInit = {}) {
  const deadlineCancellation = new AbortController();
  const deadlineTimer = setTimeout(() => deadlineCancellation.abort(), PRAZO_MAXIMO_DE_ESPERA_DA_TELA_EM_MS);
  try {
    const httpResponse = await fetch(apiRoute, { ...requestOptions, signal: deadlineCancellation.signal });
    const responseBody: unknown = await httpResponse.json().catch(() => undefined);
    return { httpResponse, apiAnswer: convertBodyToApiAnswer<ResponseData>(httpResponse, responseBody) };
  } finally {
    clearTimeout(deadlineTimer);
  }
}

export async function enviarOrdem(ordemParaEnviar: OrdemParaEnviar): Promise<RespostaDaOrdem> {
  let orderCreationCall: Awaited<ReturnType<typeof callOrderGeneratorApiWithDeadline<OrderResponseData>>>;
  try {
    orderCreationCall = await callOrderGeneratorApiWithDeadline<OrderResponseData>(ROTA_DE_CRIACAO_DE_ORDEM, {
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

  const { httpResponse, apiAnswer } = orderCreationCall;
  const answeredOrder = apiAnswer?.success ? apiAnswer.data : null;
  if (httpResponse.ok && (answeredOrder?.status === 'accepted' || answeredOrder?.status === 'rejected')) {
    return {
      situacao: answeredOrder.status === 'accepted' ? 'aceita' : 'rejeitada',
      mensagemDoServidor: apiAnswer?.message ?? '',
      clOrdId: answeredOrder.clOrdId ?? '',
      orderId: answeredOrder.orderId ?? '',
      simbolo: answeredOrder.symbol ?? ordemParaEnviar.simbolo,
      lado: answeredOrder.side === 'sell' ? 'Venda' : 'Compra',
      quantidade: answeredOrder.quantity ?? ordemParaEnviar.quantidade,
      precoEmReais: answeredOrder.price ?? ordemParaEnviar.precoEmCentavos / 100,
    };
  }
  if (httpResponse.status === 400 && apiAnswer?.success === false) {
    return {
      situacao: 'invalida',
      mensagemDoServidor: apiAnswer.message || 'A ordem tem campos inválidos.',
      errosDeCampo: apiAnswer.errors,
    };
  }
  // 503 (sem sessão FIX ou sem resposta em 5 s) e corpo que não chegou são falta de resposta; outro status de erro é resposta com erro.
  const serverAnsweredWithError = httpResponse.status >= 400 && httpResponse.status !== 503;
  return {
    situacao: 'falha-de-comunicacao',
    mensagemDoServidor: serverAnsweredWithError ? MENSAGEM_DE_ERRO_INESPERADO_NO_SERVIDOR : MENSAGEM_DE_ORDEM_NAO_CONFIRMADA,
  };
}

export async function lerExposicoes(): Promise<ExposicaoDoSimbolo[]> {
  const { httpResponse, apiAnswer } = await callOrderGeneratorApiWithDeadline<ExposuresResponseData>(ROTA_DAS_EXPOSICOES).catch(() => {
    throw new Error(MENSAGEM_DE_EXPOSICAO_INDISPONIVEL);
  });
  const symbolExposures = apiAnswer?.success ? apiAnswer.data?.exposures : undefined;
  if (!httpResponse.ok || !symbolExposures) {
    throw new Error(MENSAGEM_DE_EXPOSICAO_INDISPONIVEL);
  }
  return symbolExposures.map((symbolExposure) => ({
    simbolo: symbolExposure.symbol,
    exposicao: symbolExposure.exposure,
    restanteAteOLimite: symbolExposure.remaining,
  }));
}

export async function listarOrdens(pagina: number): Promise<PaginaDeOrdens> {
  const { httpResponse, apiAnswer } = await callOrderGeneratorApiWithDeadline<OrdersPageResponseData>(
    `${ROTA_DAS_ORDENS}?page=${pagina}`,
  ).catch(() => {
    throw new Error(MENSAGEM_DE_LISTA_DE_ORDENS_INDISPONIVEL);
  });
  const ordersPage = apiAnswer?.success ? apiAnswer.data : null;
  if (!httpResponse.ok || !Array.isArray(ordersPage?.orders) || typeof ordersPage.total !== 'number') {
    throw new Error(MENSAGEM_DE_LISTA_DE_ORDENS_INDISPONIVEL);
  }
  return {
    pagina: ordersPage.page ?? pagina,
    totalDeOrdens: ordersPage.total,
    ordens: ordersPage.orders.map((storedOrder) => ({
      recebidaEm: storedOrder.receivedAt,
      situacao: storedOrder.status === 'accepted' ? 'aceita' : 'rejeitada',
      simbolo: storedOrder.symbol,
      lado: storedOrder.side === 'buy' ? 'Compra' : storedOrder.side === 'sell' ? 'Venda' : null,
      quantidade: storedOrder.quantity,
      precoEmReais: storedOrder.price,
      orderId: storedOrder.orderId,
      clOrdId: storedOrder.clOrdId,
    })),
  };
}

export async function apagarTodasAsOrdens(): Promise<void> {
  const { httpResponse } = await callOrderGeneratorApiWithDeadline(ROTA_DAS_ORDENS, { method: 'DELETE' }).catch(() => {
    throw new Error(MENSAGEM_DE_ORDENS_NAO_APAGADAS);
  });
  if (!httpResponse.ok) throw new Error(MENSAGEM_DE_ORDENS_NAO_APAGADAS);
}
