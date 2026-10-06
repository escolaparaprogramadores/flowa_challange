import type { OrderSide, OrderTicketSymbol } from '../lib/order-validation/orderValidation';

// Routes and formats: docs/contracts/contracts.md, section 1.
export const CREATE_ORDER_ROUTE = '/api/orders';
export const EXPOSURES_ROUTE = '/api/exposures';
export const ORDERS_ROUTE = '/api/orders';

// The OrderGenerator already answers 503 after 5 s without a reply from the OrderAccumulator.
// This deadline is the safety net so the screen never hangs if the Generator itself goes silent.
export const SCREEN_MAX_WAIT_IN_MS = 6_000;

// Without a confirmed answer the screen cannot know whether the order arrived: it only says what is certain, with no internal service name.
export const UNCONFIRMED_ORDER_MESSAGE = 'A ordem não foi confirmada: o servidor de ordens não respondeu. Tente de novo em instantes.';
export const UNEXPECTED_SERVER_ERROR_MESSAGE = 'A ordem não foi confirmada: o servidor de ordens teve um erro inesperado. Tente de novo em instantes.';
export const EXPOSURE_UNAVAILABLE_MESSAGE = 'Não foi possível ler a exposição agora. Tente de novo em instantes.';
export const ORDER_LIST_UNAVAILABLE_MESSAGE = 'Não foi possível ler as ordens agora. Tente de novo em instantes.';
export const ORDERS_NOT_DELETED_MESSAGE = 'Não foi possível apagar as ordens agora. Tente de novo em instantes.';

// In the test mode the ticket hands over the fields as typed, so the server is the one that validates them.
// "mode" is optional only on the normal order: the ticket that does not know the test mode yet keeps compiling.
export type OrderToSend =
  | { mode?: 'normal'; symbol: OrderTicketSymbol; side: OrderSide; quantity: number; priceInCents: number }
  | { mode: 'test'; symbol: string; side: OrderSide; quantityText: string; priceText: string };

export type OrderSendResult =
  | {
      outcome: 'accepted' | 'rejected';
      serverMessage: string;
      clOrdId: string;
      orderId: string;
      symbol: string;
      side: OrderSide;
      quantity: number;
      priceInReais: number;
    }
  | { outcome: 'invalid'; serverMessage: string; fieldErrors: string[] }
  | { outcome: 'communication-failure'; serverMessage: string };

export type SymbolExposure = { symbol: string; exposure: number; remainingToLimit: number };

export type ListedOrder = {
  receivedAt: string;
  outcome: 'accepted' | 'rejected';
  symbol: string | null;
  side: OrderSide | null;
  quantity: number;
  priceInReais: number;
  orderId: string;
  clOrdId: string;
};

export type OrderListPage = { page: number; totalOrders: number; orders: ListedOrder[] };

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

// The deadline lasts until the body finishes arriving: a server that sends the headers and stalls
// on the body is also abandoned. An empty, HTML or cut body becomes "no body".
async function callOrderGeneratorApiWithDeadline<ResponseData>(apiRoute: string, requestOptions: RequestInit = {}) {
  const deadlineCancellation = new AbortController();
  const deadlineTimer = setTimeout(() => deadlineCancellation.abort(), SCREEN_MAX_WAIT_IN_MS);
  try {
    const httpResponse = await fetch(apiRoute, { ...requestOptions, signal: deadlineCancellation.signal });
    const responseBody: unknown = await httpResponse.json().catch(() => undefined);
    return { httpResponse, apiAnswer: convertBodyToApiAnswer<ResponseData>(httpResponse, responseBody) };
  } finally {
    clearTimeout(deadlineTimer);
  }
}

// Test mode: only the decimal comma becomes a dot; everything else goes as typed, as text (decision 15).
function replaceDecimalCommaWithDot(typedField: string) {
  return typedField.replace(',', '.');
}

function buildOrderRequestBody(orderToSend: OrderToSend) {
  if (orderToSend.mode === 'test') {
    return {
      symbol: orderToSend.symbol,
      side: orderToSend.side,
      quantity: replaceDecimalCommaWithDot(orderToSend.quantityText),
      price: replaceDecimalCommaWithDot(orderToSend.priceText),
    };
  }
  return {
    symbol: orderToSend.symbol,
    side: orderToSend.side,
    quantity: orderToSend.quantity,
    price: orderToSend.priceInCents / 100,
  };
}

export async function sendOrder(orderToSend: OrderToSend): Promise<OrderSendResult> {
  const orderRequestBody = buildOrderRequestBody(orderToSend);
  let orderCreationCall: Awaited<ReturnType<typeof callOrderGeneratorApiWithDeadline<OrderResponseData>>>;
  try {
    orderCreationCall = await callOrderGeneratorApiWithDeadline<OrderResponseData>(CREATE_ORDER_ROUTE, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(orderRequestBody),
    });
  } catch {
    return { outcome: 'communication-failure', serverMessage: UNCONFIRMED_ORDER_MESSAGE };
  }

  const { httpResponse, apiAnswer } = orderCreationCall;
  const answeredOrder = apiAnswer?.success ? apiAnswer.data : null;
  if (httpResponse.ok && (answeredOrder?.status === 'accepted' || answeredOrder?.status === 'rejected')) {
    return {
      outcome: answeredOrder.status,
      serverMessage: apiAnswer?.message ?? '',
      clOrdId: answeredOrder.clOrdId ?? '',
      orderId: answeredOrder.orderId ?? '',
      symbol: answeredOrder.symbol ?? orderToSend.symbol,
      side: answeredOrder.side === 'sell' ? 'sell' : 'buy',
      quantity: answeredOrder.quantity ?? Number(orderRequestBody.quantity),
      priceInReais: answeredOrder.price ?? Number(orderRequestBody.price),
    };
  }
  if (httpResponse.status === 400 && apiAnswer?.success === false) {
    return {
      outcome: 'invalid',
      serverMessage: apiAnswer.message || 'A ordem tem campos inválidos.',
      fieldErrors: apiAnswer.errors,
    };
  }
  // 503 (no FIX session or no answer within 5 s) and a body that never arrived mean no answer; any other error status is an answer with an error.
  const serverAnsweredWithError = httpResponse.status >= 400 && httpResponse.status !== 503;
  return {
    outcome: 'communication-failure',
    serverMessage: serverAnsweredWithError ? UNEXPECTED_SERVER_ERROR_MESSAGE : UNCONFIRMED_ORDER_MESSAGE,
  };
}

export async function fetchExposures(): Promise<SymbolExposure[]> {
  const { httpResponse, apiAnswer } = await callOrderGeneratorApiWithDeadline<ExposuresResponseData>(EXPOSURES_ROUTE).catch(() => {
    throw new Error(EXPOSURE_UNAVAILABLE_MESSAGE);
  });
  const symbolExposures = apiAnswer?.success ? apiAnswer.data?.exposures : undefined;
  if (!httpResponse.ok || !symbolExposures) {
    throw new Error(EXPOSURE_UNAVAILABLE_MESSAGE);
  }
  return symbolExposures.map((symbolExposure) => ({
    symbol: symbolExposure.symbol,
    exposure: symbolExposure.exposure,
    remainingToLimit: symbolExposure.remaining,
  }));
}

export async function fetchOrderListPage(requestedPage: number): Promise<OrderListPage> {
  const { httpResponse, apiAnswer } = await callOrderGeneratorApiWithDeadline<OrdersPageResponseData>(
    `${ORDERS_ROUTE}?page=${requestedPage}`,
  ).catch(() => {
    throw new Error(ORDER_LIST_UNAVAILABLE_MESSAGE);
  });
  const ordersPage = apiAnswer?.success ? apiAnswer.data : null;
  if (!httpResponse.ok || !Array.isArray(ordersPage?.orders) || typeof ordersPage.total !== 'number') {
    throw new Error(ORDER_LIST_UNAVAILABLE_MESSAGE);
  }
  return {
    page: ordersPage.page ?? requestedPage,
    totalOrders: ordersPage.total,
    orders: ordersPage.orders.map((storedOrder) => ({
      receivedAt: storedOrder.receivedAt,
      outcome: storedOrder.status === 'accepted' ? 'accepted' : 'rejected',
      symbol: storedOrder.symbol,
      side: storedOrder.side === 'buy' ? 'buy' : storedOrder.side === 'sell' ? 'sell' : null,
      quantity: storedOrder.quantity,
      priceInReais: storedOrder.price,
      orderId: storedOrder.orderId,
      clOrdId: storedOrder.clOrdId,
    })),
  };
}

export async function deleteAllOrders(): Promise<void> {
  const { httpResponse } = await callOrderGeneratorApiWithDeadline(ORDERS_ROUTE, { method: 'DELETE' }).catch(() => {
    throw new Error(ORDERS_NOT_DELETED_MESSAGE);
  });
  if (!httpResponse.ok) throw new Error(ORDERS_NOT_DELETED_MESSAGE);
}
