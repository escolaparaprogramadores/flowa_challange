import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  SCREEN_MAX_WAIT_IN_MS,
  deleteAllOrders,
  fetchExposures,
  fetchOrderListPage,
  sendOrder,
  type OrderToSend,
} from './ordersService';

const buyOrder: OrderToSend = { symbol: 'PETR4', side: 'buy', quantity: 100, priceInCents: 1_050 };

function simulateServerAnsweringWithJson(responseHttpStatus: number, responseBody: unknown) {
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify(responseBody), { status: responseHttpStatus })));
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

function simulateServerThatNeverAnswers() {
  vi.stubGlobal('fetch', vi.fn((...[, callOptions]: [string, RequestInit]) =>
    new Promise<Response>((...[, rejectCall]: [unknown, (rejectionReason: unknown) => void]) => {
      callOptions.signal?.addEventListener('abort', () => rejectCall(new DOMException('aborted', 'AbortError')));
    })));
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

const testModeOrder: OrderToSend = { mode: 'test', symbol: 'ITUB4', side: 'buy', quantityText: '1,5', priceText: '10,005' };
const buyOrderAsAttempted = { symbol: 'PETR4', side: 'buy', quantity: 100, priceInReais: 10.5 };

function simulateServerAnsweringWithProblemCode(problemHttpStatus: number, problemErrorCode: string, problemDetail: string) {
  const apiProblem = {
    type: `urn:base-investimentos:problem:${problemErrorCode}`, title: 'Serviço indisponível', status: problemHttpStatus,
    detail: problemDetail, instance: '/api/orders', success: false, errors: [],
  };
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify(apiProblem), {
    status: problemHttpStatus, headers: { 'Content-Type': 'application/problem+json' },
  })));
}

function stubFetchAnsweringAccepted() {
  const screenFetch = vi.fn(async () => new Response(JSON.stringify({ status: 'accepted' }), { status: 200 }));
  vi.stubGlobal('fetch', screenFetch);
  return screenFetch;
}

function readSentOrderBody(screenFetch: ReturnType<typeof stubFetchAnsweringAccepted>) {
  const [, callOptions] = screenFetch.mock.calls[0] as unknown as [string, RequestInit];
  return JSON.parse(String(callOptions.body));
}

describe('sendOrder', () => {
  it('sends the body in the contract format, with the side in English and the price in reais', async () => {
    const screenFetch = stubFetchAnsweringAccepted();
    await sendOrder({ ...buyOrder, side: 'sell' });
    const [calledRoute, callOptions] = screenFetch.mock.calls[0] as unknown as [string, RequestInit];
    expect(calledRoute).toBe('/api/orders');
    expect(callOptions.method).toBe('POST');
    expect(JSON.parse(String(callOptions.body))).toEqual({ symbol: 'PETR4', side: 'sell', quantity: 100, price: 10.5 });
  });

  it('RF-03: the normal order with an explicit mode sends the same numeric body', async () => {
    const screenFetch = stubFetchAnsweringAccepted();
    await sendOrder({ ...buyOrder, mode: 'normal' });
    expect(readSentOrderBody(screenFetch)).toEqual({ symbol: 'PETR4', side: 'buy', quantity: 100, price: 10.5 });
  });

  it('CA-26 and RF-02: the test mode sends the typed text, only with the decimal comma turned into a dot', async () => {
    const screenFetch = stubFetchAnsweringAccepted();
    await sendOrder(testModeOrder);
    expect(readSentOrderBody(screenFetch)).toEqual({ symbol: 'ITUB4', side: 'buy', quantity: '1.5', price: '10.005' });
  });

  it('CA-26 and RF-02: text that is not a number goes exactly as typed', async () => {
    const screenFetch = stubFetchAnsweringAccepted();
    await sendOrder({ mode: 'test', symbol: 'PETR4', side: 'sell', quantityText: 'abc', priceText: '10,00' });
    expect(readSentOrderBody(screenFetch)).toEqual({ symbol: 'PETR4', side: 'sell', quantity: 'abc', price: '10.00' });
  });

  it('CA-4: converts the accepted order DataMessage for the screen, with the server message', async () => {
    simulateServerAnsweringWithJson(200, buildSuccessDataMessage(
      { status: 'accepted', clOrdId: 'send-1', orderId: 'order-1', execId: 'exec-1', symbol: 'PETR4', side: 'sell', quantity: 100, price: 10.5 },
      'Ordem aceita.'));
    expect(await sendOrder(buyOrder)).toEqual({
      outcome: 'accepted', serverMessage: 'Ordem aceita.', clOrdId: 'send-1', orderId: 'order-1',
      symbol: 'PETR4', side: 'sell', quantity: 100, priceInReais: 10.5, attemptedOrder: buyOrderAsAttempted,
    });
  });

  it('CA-4: a rejected order is a DataMessage success and brings the tag 58 text', async () => {
    simulateServerAnsweringWithJson(200, buildSuccessDataMessage(
      { status: 'rejected', clOrdId: 'send-2', orderId: 'order-2', execId: 'exec-2', symbol: 'VIIA4', side: 'buy', quantity: 99_999, price: 999.99 },
      'Ordem rejeitada: a exposição de VIIA4 passaria do limite de 100.000.000,00.'));
    expect(await sendOrder(buyOrder)).toEqual({
      outcome: 'rejected', serverMessage: 'Ordem rejeitada: a exposição de VIIA4 passaria do limite de 100.000.000,00.',
      clOrdId: 'send-2', orderId: 'order-2', symbol: 'VIIA4', side: 'buy', quantity: 99_999, priceInReais: 999.99,
      attemptedOrder: buyOrderAsAttempted,
    });
  });

  it('RF-13: in the test mode the attempted order keeps the typed text, not a rounded number', async () => {
    simulateServerAnsweringWithJson(200, buildSuccessDataMessage(
      { status: 'rejected', clOrdId: 'send-3', orderId: 'order-3', symbol: 'ITUB4', side: 'buy', quantity: 1.5, price: 10.005 },
      'Símbolo inválido.'));
    const orderSendResult = await sendOrder(testModeOrder);
    expect(orderSendResult.attemptedOrder).toEqual({ symbol: 'ITUB4', side: 'buy', quantity: '1,5', priceInReais: '10,005' });
  });

  it('RF-25 and CA-5: translates the server 400 problem+json into the message of each field', async () => {
    simulateServerAnsweringWithProblem(400, 'A ordem tem campos inválidos.', ['O preço deve ser múltiplo de 0,01.']);
    expect(await sendOrder(buyOrder)).toEqual({
      outcome: 'invalid',
      serverMessage: 'A ordem tem campos inválidos.',
      fieldErrors: ['O preço deve ser múltiplo de 0,01.'],
      attemptedOrder: buyOrderAsAttempted,
    });
  });

  it('CA-26 and RF-07: the 400 of a quantity "abc" brings the field message and the typed text', async () => {
    simulateServerAnsweringWithProblem(400, 'A ordem tem campos inválidos.', ['A quantidade deve ser um número inteiro.']);
    expect(await sendOrder({ mode: 'test', symbol: 'PETR4', side: 'buy', quantityText: 'abc', priceText: '10,00' })).toEqual({
      outcome: 'invalid',
      serverMessage: 'A ordem tem campos inválidos.',
      fieldErrors: ['A quantidade deve ser um número inteiro.'],
      attemptedOrder: { symbol: 'PETR4', side: 'buy', quantity: 'abc', priceInReais: '10,00' },
    });
  });

  it('G-2 and RF-08: the 422 fix-order-rejected says the order did not enter, with the reject text', async () => {
    simulateServerAnsweringWithProblemCode(422, 'fix-order-rejected', 'Required tag missing (35=3, 371=54).');
    expect(await sendOrder(buyOrder)).toEqual({
      outcome: 'not-entered', serverMessage: 'Required tag missing (35=3, 371=54).', attemptedOrder: buyOrderAsAttempted,
    });
  });

  for (const maybeAcceptedErrorCode of ['execution-report-timeout', 'fix-session-lost']) {
    it(`CA-11 and RF-10: the 503 ${maybeAcceptedErrorCode} says the order may have been accepted, never "try again"`, async () => {
      simulateServerAnsweringWithProblemCode(503, maybeAcceptedErrorCode, 'A ordem pode ter sido aceita. Confira a lista antes de enviar de novo.');
      const orderSendResult = await sendOrder(buyOrder);
      expect(orderSendResult).toEqual({
        outcome: 'maybe-accepted',
        serverMessage: 'A ordem pode ter sido aceita. Confira a lista antes de enviar de novo.',
        attemptedOrder: buyOrderAsAttempted,
      });
      expect(orderSendResult.serverMessage).not.toContain('Tente de novo');
    });
  }

  it('CA-23 and RF-09: the 503 without a FIX session stays a communication failure, without the internal service name', async () => {
    simulateServerAnsweringWithProblemCode(503, 'fix-session-not-logged-on', 'Não foi possível falar com o OrderAccumulator. Tente de novo em instantes.');
    expect(await sendOrder(buyOrder)).toEqual({
      outcome: 'communication-failure',
      serverMessage: 'A ordem não foi confirmada: o servidor de ordens não respondeu. Tente de novo em instantes.',
      attemptedOrder: buyOrderAsAttempted,
    });
  });

  it('RF-23: a server 503 of another code becomes "order not confirmed", without the internal service name', async () => {
    simulateServerAnsweringWithProblem(503, 'Não foi possível falar com o OrderAccumulator. Tente de novo em instantes.');
    expect(await sendOrder(buyOrder)).toEqual({
      outcome: 'communication-failure',
      serverMessage: 'A ordem não foi confirmada: o servidor de ordens não respondeu. Tente de novo em instantes.',
      attemptedOrder: buyOrderAsAttempted,
    });
  });

  it('RF-09: a call that fails before any answer (network down) is a communication failure', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => { throw new TypeError('Failed to fetch'); }));
    expect(await sendOrder(buyOrder)).toEqual({
      outcome: 'communication-failure',
      serverMessage: 'A ordem não foi confirmada: o servidor de ordens não respondeu. Tente de novo em instantes.',
      attemptedOrder: buyOrderAsAttempted,
    });
  });

  it('RF-23: a proxy 502 with an HTML body becomes "unexpected error", not "did not answer"', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response('<html>Bad Gateway</html>', { status: 502 })));
    expect(await sendOrder(buyOrder)).toEqual({
      outcome: 'communication-failure',
      serverMessage: 'A ordem não foi confirmada: o servidor de ordens teve um erro inesperado. Tente de novo em instantes.',
      attemptedOrder: buyOrderAsAttempted,
    });
  });

  it('RF-23: a 404 outside the contract is also an answer with an error, not "did not answer"', async () => {
    simulateServerAnsweringWithJson(404, {});
    expect(await sendOrder(buyOrder)).toEqual({
      outcome: 'communication-failure',
      serverMessage: 'A ordem não foi confirmada: o servidor de ordens teve um erro inesperado. Tente de novo em instantes.',
      attemptedOrder: buyOrderAsAttempted,
    });
  });

  it('RF-23: the contract 500 becomes "unexpected error", because the server answered', async () => {
    simulateServerAnsweringWithProblem(500, 'Aconteceu um erro inesperado. Informe o traceId ao suporte.');
    expect(await sendOrder(buyOrder)).toEqual({
      outcome: 'communication-failure',
      serverMessage: 'A ordem não foi confirmada: o servidor de ordens teve um erro inesperado. Tente de novo em instantes.',
      attemptedOrder: buyOrderAsAttempted,
    });
  });

  it('CA-11 and RF-10: a server that does not answer is awaited for 6 s, and no less, and the order may have been accepted', async () => {
    vi.useFakeTimers();
    simulateServerThatNeverAnswers();
    let orderSendFinished = false;
    const pendingOrderSend = sendOrder(buyOrder).finally(() => { orderSendFinished = true; });
    await vi.advanceTimersByTimeAsync(SCREEN_MAX_WAIT_IN_MS - 1);
    expect(orderSendFinished).toBe(false);
    await vi.advanceTimersByTimeAsync(1);
    expect(await pendingOrderSend).toEqual({
      outcome: 'maybe-accepted',
      serverMessage: 'A ordem pode ter sido aceita. Confira a lista antes de enviar de novo.',
      attemptedOrder: buyOrderAsAttempted,
    });
    expect(SCREEN_MAX_WAIT_IN_MS).toBe(6_000);
  });

  it('CA-11 and RF-10: headers that arrive with a stalled body are also abandoned at the deadline, as "may have been accepted"', async () => {
    vi.useFakeTimers();
    const fetchWithStalledBody = async (...[, callOptions]: [string, RequestInit]) => {
      const bodyThatNeverEnds = new ReadableStream({
        start(bodyController) {
          callOptions.signal?.addEventListener('abort', () => bodyController.error(new DOMException('aborted', 'AbortError')));
        },
      });
      return new Response(bodyThatNeverEnds, { status: 200 });
    };
    vi.stubGlobal('fetch', vi.fn(fetchWithStalledBody));
    const pendingOrderSend = sendOrder(buyOrder);
    await vi.advanceTimersByTimeAsync(SCREEN_MAX_WAIT_IN_MS);
    expect(await pendingOrderSend).toEqual({
      outcome: 'maybe-accepted',
      serverMessage: 'A ordem pode ter sido aceita. Confira a lista antes de enviar de novo.',
      attemptedOrder: buyOrderAsAttempted,
    });
  });
});

describe('fetchExposures', () => {
  it('converts the contract body for the screen, in the received order', async () => {
    simulateServerAnsweringWithJson(200, buildSuccessDataMessage(
      { limit: 100_000_000, exposures: [{ symbol: 'PETR4', exposure: -500, remaining: 99_999_500 }] }, 'Exposição dos símbolos lida.'));
    expect(await fetchExposures()).toEqual([{ symbol: 'PETR4', exposure: -500, remainingToLimit: 99_999_500 }]);
  });

  it('RF-32: a server 503 becomes a clear error, without the internal service name', async () => {
    simulateServerAnsweringWithProblem(503, 'Não foi possível falar com o OrderAccumulator. Tente de novo em instantes.');
    await expect(fetchExposures()).rejects.toThrow('Não foi possível ler a exposição agora. Tente de novo em instantes.');
  });
});

describe('fetchOrderListPage', () => {
  const contractPage = {
    page: 1,
    pageSize: 10,
    total: 2,
    orders: [
      { receivedAt: '2026-10-04T09:34:23.390427Z', status: 'accepted', symbol: 'PETR4', side: 'buy', quantity: 1_000, price: 12.34, orderId: 'order-2', clOrdId: 'send-2' },
      { receivedAt: '2026-10-04T09:30:00Z', status: 'rejected', symbol: null, side: null, quantity: 5, price: 1.1, orderId: 'order-1', clOrdId: 'send-1' },
    ],
  };

  it('CA-41: reads only the requested page, with one GET call to /api/orders?page=<n>', async () => {
    const simulatedOrderListServer = vi.fn(async () =>
      new Response(JSON.stringify(buildSuccessDataMessage(contractPage, 'Página de ordens lida.')), { status: 200 }));
    vi.stubGlobal('fetch', simulatedOrderListServer);
    await fetchOrderListPage(1);
    expect(simulatedOrderListServer).toHaveBeenCalledTimes(1);
    const [calledRoute, callOptions] = simulatedOrderListServer.mock.calls[0] as unknown as [string, RequestInit];
    expect(calledRoute).toBe('/api/orders?page=1');
    expect(callOptions.method).toBeUndefined();
  });

  it('CA-11: converts the contract body for the screen, in the received order, with null side and symbol as null', async () => {
    simulateServerAnsweringWithJson(200, buildSuccessDataMessage(contractPage, 'Página de ordens lida.'));
    expect(await fetchOrderListPage(1)).toEqual({
      page: 1,
      totalOrders: 2,
      orders: [
        { receivedAt: '2026-10-04T09:34:23.390427Z', outcome: 'accepted', symbol: 'PETR4', side: 'buy', quantity: 1_000, priceInReais: 12.34, orderId: 'order-2', clOrdId: 'send-2' },
        { receivedAt: '2026-10-04T09:30:00Z', outcome: 'rejected', symbol: null, side: null, quantity: 5, priceInReais: 1.1, orderId: 'order-1', clOrdId: 'send-1' },
      ],
    });
  });

  it('CA-11: "sell" stays the sell side', async () => {
    simulateServerAnsweringWithJson(200, buildSuccessDataMessage(
      { ...contractPage, total: 1, orders: [{ ...contractPage.orders[0], side: 'sell' }] }, 'Página de ordens lida.'));
    expect((await fetchOrderListPage(1)).orders[0].side).toBe('sell');
  });

  it('CA-14: an empty database comes back with total 0 and no order', async () => {
    simulateServerAnsweringWithJson(200, buildSuccessDataMessage({ page: 1, pageSize: 10, total: 0, orders: [] }, 'Página de ordens lida.'));
    expect(await fetchOrderListPage(1)).toEqual({ page: 1, totalOrders: 0, orders: [] });
  });

  it('ASSUMI-01: 400 and 503 problem+json become the list unavailable message', async () => {
    for (const [problemHttpStatus, problemDetail] of [[400, 'Página inválida.'], [503, 'Não foi possível falar com o OrderAccumulator.']] as const) {
      simulateServerAnsweringWithProblem(problemHttpStatus, problemDetail);
      await expect(fetchOrderListPage(1), `status ${problemHttpStatus}`).rejects.toThrow('Não foi possível ler as ordens agora. Tente de novo em instantes.');
    }
  });

  it('ASSUMI-01: a 200 with a body outside the DataMessage becomes the list unavailable message', async () => {
    for (const responseBodyOutsideTheContract of [{ status: 'ok' }, contractPage]) {
      simulateServerAnsweringWithJson(200, responseBodyOutsideTheContract);
      await expect(fetchOrderListPage(1)).rejects.toThrow('Não foi possível ler as ordens agora. Tente de novo em instantes.');
    }
  });

  it('ASSUMI-01: a server that does not answer within 6 s becomes list unavailable', async () => {
    vi.useFakeTimers();
    simulateServerThatNeverAnswers();
    const pendingOrderListPage = fetchOrderListPage(1);
    const failureCheck = expect(pendingOrderListPage).rejects.toThrow('Não foi possível ler as ordens agora. Tente de novo em instantes.');
    await vi.advanceTimersByTimeAsync(SCREEN_MAX_WAIT_IN_MS);
    await failureCheck;
  });
});

describe('deleteAllOrders', () => {
  it('sends DELETE to /api/orders and ends without error on 204', async () => {
    const simulatedDeleteServer = vi.fn(async () => new Response(null, { status: 204 }));
    vi.stubGlobal('fetch', simulatedDeleteServer);
    await expect(deleteAllOrders()).resolves.toBeUndefined();
    expect(simulatedDeleteServer).toHaveBeenCalledTimes(1);
    const [calledRoute, callOptions] = simulatedDeleteServer.mock.calls[0] as unknown as [string, RequestInit];
    expect(calledRoute).toBe('/api/orders');
    expect(callOptions.method).toBe('DELETE');
  });

  it('a server 400 also throws the delete error, without passing on the raw body', async () => {
    simulateServerAnsweringWithProblem(400, 'Pedido inválido.');
    await expect(deleteAllOrders()).rejects.toThrow('Não foi possível apagar as ordens agora. Tente de novo em instantes.');
  });

  it('a server 503 throws an error with a clear message, without the internal service name', async () => {
    simulateServerAnsweringWithProblem(503, 'Não foi possível falar com o OrderAccumulator.');
    await expect(deleteAllOrders()).rejects.toThrow('Não foi possível apagar as ordens agora. Tente de novo em instantes.');
  });

  it('a server that does not answer within 6 s throws the same error', async () => {
    vi.useFakeTimers();
    simulateServerThatNeverAnswers();
    const pendingDelete = deleteAllOrders();
    const failureCheck = expect(pendingDelete).rejects.toThrow('Não foi possível apagar as ordens agora. Tente de novo em instantes.');
    await vi.advanceTimersByTimeAsync(SCREEN_MAX_WAIT_IN_MS);
    await failureCheck;
  });
});
