import type { LadoDaOrdem, SimboloDaBoleta } from './lib/validacaoDaOrdem';

// Rotas e formatos: docs/contracts/contracts.md, seção 1.
export const ROTA_DE_CRIACAO_DE_ORDEM = '/api/orders';
export const ROTA_DAS_EXPOSICOES = '/api/exposures';

// O OrderGenerator já responde 503 depois de 5 s sem resposta do OrderAccumulator.
// Este prazo é só uma rede de segurança para a tela nunca ficar presa esperando.
const PRAZO_MAXIMO_DE_ESPERA_DA_TELA_EM_MS = 7_000;

const MENSAGEM_DE_SERVIDOR_SEM_RESPOSTA = 'Não foi possível falar com o servidor. Tente de novo em instantes.';
const MENSAGEM_DE_EXPOSICAO_INDISPONIVEL = 'Não foi possível ler a exposição. Tente de novo em instantes.';

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
  status?: string;
  message?: string;
  clOrdId?: string;
  orderId?: string;
  symbol?: string;
  side?: string;
  quantity?: number;
  price?: number;
  errors?: Array<{ field: string; message: string }>;
};

type CorpoDasExposicoes = {
  limit?: number;
  exposures?: Array<{ symbol: string; exposure: number; remaining: number }>;
  message?: string;
};

async function chamarApiComPrazo(rotaDaApi: string, opcoesDaRequisicao: RequestInit = {}): Promise<Response> {
  const cancelamentoPorPrazo = new AbortController();
  const temporizadorDoPrazo = setTimeout(() => cancelamentoPorPrazo.abort(), PRAZO_MAXIMO_DE_ESPERA_DA_TELA_EM_MS);
  try {
    return await fetch(rotaDaApi, { ...opcoesDaRequisicao, signal: cancelamentoPorPrazo.signal });
  } finally {
    clearTimeout(temporizadorDoPrazo);
  }
}

// Proxy ou erro de rede podem devolver corpo vazio ou HTML; nesses casos vale a mensagem padrão.
async function lerCorpoJsonOuNada<CorpoEsperado>(respostaHttp: Response): Promise<CorpoEsperado | undefined> {
  try {
    return (await respostaHttp.json()) as CorpoEsperado;
  } catch {
    return undefined;
  }
}

export async function enviarOrdem(ordem: OrdemParaEnviar): Promise<RespostaDaOrdem> {
  let respostaHttp: Response;
  try {
    respostaHttp = await chamarApiComPrazo(ROTA_DE_CRIACAO_DE_ORDEM, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        symbol: ordem.simbolo,
        side: ordem.lado === 'Compra' ? 'buy' : 'sell',
        quantity: ordem.quantidade,
        price: ordem.precoEmCentavos / 100,
      }),
    });
  } catch {
    return { situacao: 'falha-de-comunicacao', mensagemDoServidor: MENSAGEM_DE_SERVIDOR_SEM_RESPOSTA };
  }

  const corpoDaResposta = await lerCorpoJsonOuNada<CorpoDaRespostaDaOrdem>(respostaHttp);
  if (respostaHttp.ok && (corpoDaResposta?.status === 'accepted' || corpoDaResposta?.status === 'rejected')) {
    return {
      situacao: corpoDaResposta.status === 'accepted' ? 'aceita' : 'rejeitada',
      mensagemDoServidor: corpoDaResposta.message ?? '',
      clOrdId: corpoDaResposta.clOrdId ?? '',
      orderId: corpoDaResposta.orderId ?? '',
      simbolo: corpoDaResposta.symbol ?? ordem.simbolo,
      lado: corpoDaResposta.side === 'sell' ? 'Venda' : 'Compra',
      quantidade: corpoDaResposta.quantity ?? ordem.quantidade,
      precoEmReais: corpoDaResposta.price ?? ordem.precoEmCentavos / 100,
    };
  }
  if (respostaHttp.status === 400 && corpoDaResposta?.status === 'validation_error') {
    return {
      situacao: 'invalida',
      mensagemDoServidor: corpoDaResposta.message ?? 'A ordem tem campos inválidos.',
      errosDeCampo: (corpoDaResposta.errors ?? []).map((erroDeCampo) => erroDeCampo.message),
    };
  }
  return { situacao: 'falha-de-comunicacao', mensagemDoServidor: corpoDaResposta?.message ?? MENSAGEM_DE_SERVIDOR_SEM_RESPOSTA };
}

export async function lerExposicoes(): Promise<ExposicaoDoSimbolo[]> {
  let respostaHttp: Response;
  try {
    respostaHttp = await chamarApiComPrazo(ROTA_DAS_EXPOSICOES);
  } catch {
    throw new Error(MENSAGEM_DE_EXPOSICAO_INDISPONIVEL);
  }
  const corpoDaResposta = await lerCorpoJsonOuNada<CorpoDasExposicoes>(respostaHttp);
  if (!respostaHttp.ok || !corpoDaResposta?.exposures) {
    throw new Error(corpoDaResposta?.message ?? MENSAGEM_DE_EXPOSICAO_INDISPONIVEL);
  }
  return corpoDaResposta.exposures.map((exposicaoNoServidor) => ({
    simbolo: exposicaoNoServidor.symbol,
    exposicao: exposicaoNoServidor.exposure,
    restanteAteOLimite: exposicaoNoServidor.remaining,
  }));
}
