import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  PRAZO_MAXIMO_DE_ESPERA_DA_TELA_EM_MS,
  apagarTodasAsOrdens,
  enviarOrdem,
  lerExposicoes,
  listarOrdens,
  type OrdemParaEnviar,
} from './ordensService';

const ordemDeCompra: OrdemParaEnviar = { simbolo: 'PETR4', lado: 'Compra', quantidade: 100, precoEmCentavos: 1_050 };

function simularServidorRespondendoComJson(statusHttpDaResposta: number, corpoDaResposta: unknown) {
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify(corpoDaResposta), { status: statusHttpDaResposta })));
}

// docs/contracts/contracts.md: success is a DataMessage, every error is a problem+json.
function buildSuccessDataMessage(responseData: unknown, successMessage: string) {
  return { success: true, status: 'Ok', message: successMessage, data: responseData, errors: [], errorCode: null };
}

// The type, title and statusResultado the server really writes for each status (contracts.md, section 1).
const problemOfEachStatus: Record<number, { type: string; title: string; statusResultado: string }> = {
  400: { type: 'urn:base-investimentos:problem:invalid-order', title: 'Dados inválidos', statusResultado: 'InvalidInput' },
  500: { type: 'urn:base-investimentos:problem:internal-error', title: 'Erro interno', statusResultado: 'InternalError' },
  503: { type: 'urn:base-investimentos:problem:order-accumulator-unavailable', title: 'Serviço indisponível', statusResultado: 'ServiceUnavailable' },
};

function simulateServerAnsweringWithProblem(problemHttpStatus: 400 | 500 | 503, problemDetail: string, problemErrors: string[] = []) {
  const apiProblem = {
    ...problemOfEachStatus[problemHttpStatus], status: problemHttpStatus, detail: problemDetail,
    instance: '/api/orders', traceId: '0af7651916cd43dd8448eb211c80319c', success: false, errors: problemErrors,
  };
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify(apiProblem), {
    status: problemHttpStatus, headers: { 'Content-Type': 'application/problem+json' },
  })));
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

describe('enviarOrdem', () => {
  it('manda o corpo no formato do contrato, com lado em inglês e preço em reais', async () => {
    const fetchDaTela = vi.fn(async () => new Response(JSON.stringify({ status: 'accepted' }), { status: 200 }));
    vi.stubGlobal('fetch', fetchDaTela);
    await enviarOrdem({ ...ordemDeCompra, lado: 'Venda' });
    const [rotaChamada, opcoesDaChamada] = fetchDaTela.mock.calls[0] as unknown as [string, RequestInit];
    expect(rotaChamada).toBe('/api/orders');
    expect(opcoesDaChamada.method).toBe('POST');
    expect(JSON.parse(String(opcoesDaChamada.body))).toEqual({ symbol: 'PETR4', side: 'sell', quantity: 100, price: 10.5 });
  });

  it('CA-4: converte o DataMessage da ordem aceita para a tela, com a mensagem do servidor', async () => {
    simularServidorRespondendoComJson(200, buildSuccessDataMessage(
      { status: 'accepted', clOrdId: 'envio-1', orderId: 'ordem-1', execId: 'exec-1', symbol: 'PETR4', side: 'sell', quantity: 100, price: 10.5 },
      'Ordem aceita.'));
    expect(await enviarOrdem(ordemDeCompra)).toEqual({
      situacao: 'aceita', mensagemDoServidor: 'Ordem aceita.', clOrdId: 'envio-1', orderId: 'ordem-1',
      simbolo: 'PETR4', lado: 'Venda', quantidade: 100, precoEmReais: 10.5,
    });
  });

  it('CA-4: ordem rejeitada é sucesso do DataMessage e traz o texto da tag 58', async () => {
    simularServidorRespondendoComJson(200, buildSuccessDataMessage(
      { status: 'rejected', clOrdId: 'envio-2', orderId: 'ordem-2', execId: 'exec-2', symbol: 'VIIA4', side: 'buy', quantity: 99_999, price: 999.99 },
      'Ordem rejeitada: a exposição de VIIA4 passaria do limite de 100.000.000,00.'));
    expect(await enviarOrdem(ordemDeCompra)).toEqual({
      situacao: 'rejeitada', mensagemDoServidor: 'Ordem rejeitada: a exposição de VIIA4 passaria do limite de 100.000.000,00.',
      clOrdId: 'envio-2', orderId: 'ordem-2', simbolo: 'VIIA4', lado: 'Compra', quantidade: 99_999, precoEmReais: 999.99,
    });
  });

  it('RF-25 e CA-5: traduz o 400 problem+json do servidor nas mensagens de cada campo', async () => {
    simulateServerAnsweringWithProblem(400, 'A ordem tem campos inválidos.', ['O preço deve ser múltiplo de 0,01.']);
    expect(await enviarOrdem(ordemDeCompra)).toEqual({
      situacao: 'invalida',
      mensagemDoServidor: 'A ordem tem campos inválidos.',
      errosDeCampo: ['O preço deve ser múltiplo de 0,01.'],
    });
  });

  it('RF-23: 503 do servidor vira "ordem não confirmada", sem o nome do serviço interno', async () => {
    simulateServerAnsweringWithProblem(503, 'Não foi possível falar com o OrderAccumulator. Tente de novo em instantes.');
    expect(await enviarOrdem(ordemDeCompra)).toEqual({
      situacao: 'falha-de-comunicacao',
      mensagemDoServidor: 'A ordem não foi confirmada: o servidor de ordens não respondeu. Tente de novo em instantes.',
    });
  });

  it('RF-23: 502 de proxy com corpo HTML vira "erro inesperado", não "não respondeu"', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response('<html>Bad Gateway</html>', { status: 502 })));
    expect(await enviarOrdem(ordemDeCompra)).toEqual({
      situacao: 'falha-de-comunicacao',
      mensagemDoServidor: 'A ordem não foi confirmada: o servidor de ordens teve um erro inesperado. Tente de novo em instantes.',
    });
  });

  it('RF-23: 404 fora do contrato também é resposta com erro, não "não respondeu"', async () => {
    simularServidorRespondendoComJson(404, {});
    expect(await enviarOrdem(ordemDeCompra)).toEqual({
      situacao: 'falha-de-comunicacao',
      mensagemDoServidor: 'A ordem não foi confirmada: o servidor de ordens teve um erro inesperado. Tente de novo em instantes.',
    });
  });

  it('RF-23: 500 do contrato vira "erro inesperado", porque o servidor respondeu', async () => {
    simulateServerAnsweringWithProblem(500, 'Aconteceu um erro inesperado. Informe o traceId ao suporte.');
    expect(await enviarOrdem(ordemDeCompra)).toEqual({
      situacao: 'falha-de-comunicacao',
      mensagemDoServidor: 'A ordem não foi confirmada: o servidor de ordens teve um erro inesperado. Tente de novo em instantes.',
    });
  });

  it('RF-23: servidor que não responde é esperado até 6 s, e não menos, e vira falha de comunicação', async () => {
    vi.useFakeTimers();
    const fetchQueNuncaResponde = (...[, opcoesDaChamada]: [string, RequestInit]) =>
      new Promise<Response>((...[, recusarChamada]: [unknown, (motivoDaRecusa: unknown) => void]) => {
        opcoesDaChamada.signal?.addEventListener('abort', () => recusarChamada(new DOMException('abortada', 'AbortError')));
      });
    vi.stubGlobal('fetch', vi.fn(fetchQueNuncaResponde));
    let ordemTerminou = false;
    const respostaDaOrdemPendente = enviarOrdem(ordemDeCompra).finally(() => { ordemTerminou = true; });
    await vi.advanceTimersByTimeAsync(PRAZO_MAXIMO_DE_ESPERA_DA_TELA_EM_MS - 1);
    expect(ordemTerminou).toBe(false);
    await vi.advanceTimersByTimeAsync(1);
    expect(await respostaDaOrdemPendente).toEqual({
      situacao: 'falha-de-comunicacao',
      mensagemDoServidor: 'A ordem não foi confirmada: o servidor de ordens não respondeu. Tente de novo em instantes.',
    });
    expect(PRAZO_MAXIMO_DE_ESPERA_DA_TELA_EM_MS).toBe(6_000);
  });

  it('RF-23: cabeçalho que chega com corpo travado também é abandonado no prazo', async () => {
    vi.useFakeTimers();
    const fetchComCorpoTravado = async (...[, opcoesDaChamada]: [string, RequestInit]) => {
      const corpoQueNuncaTermina = new ReadableStream({
        start(controleDoCorpo) {
          opcoesDaChamada.signal?.addEventListener('abort', () => controleDoCorpo.error(new DOMException('abortada', 'AbortError')));
        },
      });
      return new Response(corpoQueNuncaTermina, { status: 200 });
    };
    vi.stubGlobal('fetch', vi.fn(fetchComCorpoTravado));
    const respostaDaOrdemPendente = enviarOrdem(ordemDeCompra);
    await vi.advanceTimersByTimeAsync(PRAZO_MAXIMO_DE_ESPERA_DA_TELA_EM_MS);
    expect(await respostaDaOrdemPendente).toEqual({
      situacao: 'falha-de-comunicacao',
      mensagemDoServidor: 'A ordem não foi confirmada: o servidor de ordens não respondeu. Tente de novo em instantes.',
    });
  });
});

describe('lerExposicoes', () => {
  it('converte o corpo do contrato para a tela, na ordem recebida', async () => {
    simularServidorRespondendoComJson(200, buildSuccessDataMessage(
      { limit: 100_000_000, exposures: [{ symbol: 'PETR4', exposure: -500, remaining: 99_999_500 }] }, 'Exposição dos símbolos lida.'));
    expect(await lerExposicoes()).toEqual([{ simbolo: 'PETR4', exposicao: -500, restanteAteOLimite: 99_999_500 }]);
  });

  it('RF-32: 503 do servidor vira erro claro, sem o nome do serviço interno', async () => {
    simulateServerAnsweringWithProblem(503, 'Não foi possível falar com o OrderAccumulator. Tente de novo em instantes.');
    await expect(lerExposicoes()).rejects.toThrow('Não foi possível ler a exposição agora. Tente de novo em instantes.');
  });
});

describe('listarOrdens', () => {
  const paginaDoContrato = {
    page: 1,
    pageSize: 10,
    total: 2,
    orders: [
      { receivedAt: '2026-10-04T09:34:23.390427Z', status: 'accepted', symbol: 'PETR4', side: 'buy', quantity: 1_000, price: 12.34, orderId: 'ordem-2', clOrdId: 'envio-2' },
      { receivedAt: '2026-10-04T09:30:00Z', status: 'rejected', symbol: null, side: null, quantity: 5, price: 1.1, orderId: 'ordem-1', clOrdId: 'envio-1' },
    ],
  };

  it('CA-41: lê só a página pedida, com uma chamada GET em /api/orders?page=<n>', async () => {
    const servidorSimuladoDaLista = vi.fn(async () =>
      new Response(JSON.stringify(buildSuccessDataMessage(paginaDoContrato, 'Página de ordens lida.')), { status: 200 }));
    vi.stubGlobal('fetch', servidorSimuladoDaLista);
    await listarOrdens(1);
    expect(servidorSimuladoDaLista).toHaveBeenCalledTimes(1);
    const [rotaChamada, opcoesDaChamada] = servidorSimuladoDaLista.mock.calls[0] as unknown as [string, RequestInit];
    expect(rotaChamada).toBe('/api/orders?page=1');
    expect(opcoesDaChamada.method).toBeUndefined();
  });

  it('CA-11: converte o corpo do contrato para a tela, na ordem recebida, com lado e símbolo nulos como null', async () => {
    simularServidorRespondendoComJson(200, buildSuccessDataMessage(paginaDoContrato, 'Página de ordens lida.'));
    expect(await listarOrdens(1)).toEqual({
      pagina: 1,
      totalDeOrdens: 2,
      ordens: [
        { recebidaEm: '2026-10-04T09:34:23.390427Z', situacao: 'aceita', simbolo: 'PETR4', lado: 'Compra', quantidade: 1_000, precoEmReais: 12.34, orderId: 'ordem-2', clOrdId: 'envio-2' },
        { recebidaEm: '2026-10-04T09:30:00Z', situacao: 'rejeitada', simbolo: null, lado: null, quantidade: 5, precoEmReais: 1.1, orderId: 'ordem-1', clOrdId: 'envio-1' },
      ],
    });
  });

  it('CA-11: "sell" vira Venda', async () => {
    simularServidorRespondendoComJson(200, buildSuccessDataMessage(
      { ...paginaDoContrato, total: 1, orders: [{ ...paginaDoContrato.orders[0], side: 'sell' }] }, 'Página de ordens lida.'));
    expect((await listarOrdens(1)).ordens[0].lado).toBe('Venda');
  });

  it('CA-14: banco vazio volta total 0 e nenhuma ordem', async () => {
    simularServidorRespondendoComJson(200, buildSuccessDataMessage({ page: 1, pageSize: 10, total: 0, orders: [] }, 'Página de ordens lida.'));
    expect(await listarOrdens(1)).toEqual({ pagina: 1, totalDeOrdens: 0, ordens: [] });
  });

  it('ASSUMI-01: 400 e 503 problem+json viram a mensagem de lista indisponível', async () => {
    for (const [problemHttpStatus, problemDetail] of [[400, 'Página inválida.'], [503, 'Não foi possível falar com o OrderAccumulator.']] as const) {
      simulateServerAnsweringWithProblem(problemHttpStatus, problemDetail);
      await expect(listarOrdens(1), `status ${problemHttpStatus}`).rejects.toThrow('Não foi possível ler as ordens agora. Tente de novo em instantes.');
    }
  });

  it('ASSUMI-01: 200 com corpo fora do DataMessage vira a mensagem de lista indisponível', async () => {
    for (const responseBodyOutsideTheContract of [{ status: 'ok' }, paginaDoContrato]) {
      simularServidorRespondendoComJson(200, responseBodyOutsideTheContract);
      await expect(listarOrdens(1)).rejects.toThrow('Não foi possível ler as ordens agora. Tente de novo em instantes.');
    }
  });

  it('ASSUMI-01: servidor que não responde em 6 s vira lista indisponível', async () => {
    vi.useFakeTimers();
    vi.stubGlobal('fetch', vi.fn((...[, opcoesDaChamada]: [string, RequestInit]) =>
      new Promise<Response>((...[, recusarChamada]: [unknown, (motivoDaRecusa: unknown) => void]) => {
        opcoesDaChamada.signal?.addEventListener('abort', () => recusarChamada(new DOMException('abortada', 'AbortError')));
      })));
    const listaPendente = listarOrdens(1);
    const verificacaoDaFalha = expect(listaPendente).rejects.toThrow('Não foi possível ler as ordens agora. Tente de novo em instantes.');
    await vi.advanceTimersByTimeAsync(PRAZO_MAXIMO_DE_ESPERA_DA_TELA_EM_MS);
    await verificacaoDaFalha;
  });
});

describe('apagarTodasAsOrdens', () => {
  it('manda DELETE em /api/orders e termina sem erro no 204', async () => {
    const servidorSimuladoDoApagamento = vi.fn(async () => new Response(null, { status: 204 }));
    vi.stubGlobal('fetch', servidorSimuladoDoApagamento);
    await expect(apagarTodasAsOrdens()).resolves.toBeUndefined();
    expect(servidorSimuladoDoApagamento).toHaveBeenCalledTimes(1);
    const [rotaChamada, opcoesDaChamada] = servidorSimuladoDoApagamento.mock.calls[0] as unknown as [string, RequestInit];
    expect(rotaChamada).toBe('/api/orders');
    expect(opcoesDaChamada.method).toBe('DELETE');
  });

  it('400 do servidor também lança o erro de apagamento, sem repassar o corpo cru', async () => {
    simulateServerAnsweringWithProblem(400, 'Pedido inválido.');
    await expect(apagarTodasAsOrdens()).rejects.toThrow('Não foi possível apagar as ordens agora. Tente de novo em instantes.');
  });

  it('503 do servidor lança erro com mensagem clara, sem o nome do serviço interno', async () => {
    simulateServerAnsweringWithProblem(503, 'Não foi possível falar com o OrderAccumulator.');
    await expect(apagarTodasAsOrdens()).rejects.toThrow('Não foi possível apagar as ordens agora. Tente de novo em instantes.');
  });

  it('servidor que não responde em 6 s lança o mesmo erro', async () => {
    vi.useFakeTimers();
    vi.stubGlobal('fetch', vi.fn((...[, opcoesDaChamada]: [string, RequestInit]) =>
      new Promise<Response>((...[, recusarChamada]: [unknown, (motivoDaRecusa: unknown) => void]) => {
        opcoesDaChamada.signal?.addEventListener('abort', () => recusarChamada(new DOMException('abortada', 'AbortError')));
      })));
    const apagamentoPendente = apagarTodasAsOrdens();
    const verificacaoDaFalha = expect(apagamentoPendente).rejects.toThrow('Não foi possível apagar as ordens agora. Tente de novo em instantes.');
    await vi.advanceTimersByTimeAsync(PRAZO_MAXIMO_DE_ESPERA_DA_TELA_EM_MS);
    await verificacaoDaFalha;
  });
});
