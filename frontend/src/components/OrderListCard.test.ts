import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { OrderListCard } from './OrderListCard';
import type { OrderListState } from '../hooks/useOrdersAndExposures';
import type { ListedOrder, OrderSendResult } from '../services/ordersService';

// The slots are the contract with F7 (pagination in the footer) and F8 (Deletar tudo in the header).
const TEST_HEADER_ACTION = createElement('button', { type: 'button', 'data-testid': 'test-header-action' }, 'Deletar tudo');
const TEST_FOOTER = createElement('nav', { 'data-testid': 'test-footer' }, 'Página 1');

const acceptedListedOrder: ListedOrder = {
  receivedAt: '2026-10-04T23:30:00Z', outcome: 'accepted', symbol: 'PETR4', side: 'buy',
  quantity: 1_000, priceInReais: 10, orderId: 'a'.repeat(32), clOrdId: 'b'.repeat(32),
};

const ORDER_LIST_STATES: Array<[string, OrderListState]> = [
  ['loading', { status: 'loading' }],
  ['failed', { status: 'error', errorMessage: 'Não foi possível ler as ordens agora. Tente de novo em instantes.' }],
  ['empty', { status: 'ready', orderListPage: { page: 1, totalOrders: 0, orders: [] } }],
  ['with orders', { status: 'ready', orderListPage: { page: 1, totalOrders: 1, orders: [acceptedListedOrder] } }],
];

function renderOrderListCard(orderListState: OrderListState) {
  return renderToStaticMarkup(
    createElement(OrderListCard, { orderListState, isSendingOrder: false, headerAction: TEST_HEADER_ACTION, footer: TEST_FOOTER }),
  );
}

describe('OrderListCard: contract slots', () => {
  for (const [stateName, orderListState] of ORDER_LIST_STATES) {
    it(`RF-15: with the list ${stateName}, the header action shows inside the card header, once`, () => {
      const cardHtml = renderOrderListCard(orderListState);
      const cardHeaderHtml = cardHtml.match(/<div class="order-list-header">([\s\S]*?)<\/div>/)?.[1] ?? '';
      expect(cardHeaderHtml).toContain('data-testid="test-header-action"');
      expect(cardHtml.split('data-testid="test-header-action"')).toHaveLength(2);
    });
  }

  it('RF-15: with orders, the footer shows once, after the table', () => {
    const cardHtml = renderOrderListCard(ORDER_LIST_STATES[3][1]);
    expect(cardHtml.split('data-testid="test-footer"')).toHaveLength(2);
    expect(cardHtml.indexOf('data-testid="test-footer"')).toBeGreaterThan(cardHtml.indexOf('</table>'));
  });

  for (const [stateName, orderListState] of ORDER_LIST_STATES.slice(0, 3)) {
    it(`ASSUMI-05: with the list ${stateName}, the footer is not rendered (CA-14: no pagination)`, () => {
      expect(renderOrderListCard(orderListState)).not.toContain('data-testid="test-footer"');
    });
  }

  it('F6: the order row keeps the pt-BR labels for outcome and side', () => {
    const cardHtml = renderOrderListCard(ORDER_LIST_STATES[3][1]);
    expect(cardHtml).toContain('<span class="order-badge order-badge-accepted">Aceita</span>');
    expect(cardHtml).toContain('<td data-column="side" data-label="Lado">Compra</td>');
  });
});

const petr4BuyAttempt = { symbol: 'PETR4', side: 'buy', quantity: 100, priceInReais: 10 } as const;

function renderOrderListCardAfterSend(lastSendResult: OrderSendResult) {
  return renderToStaticMarkup(
    createElement(OrderListCard, { orderListState: ORDER_LIST_STATES[3][1], isSendingOrder: false, lastSendResult }),
  );
}

function readResponseBoxText(cardHtml: string, testId: string) {
  return cardHtml.match(new RegExp(`data-testid="${testId}">([^<]*)<`))?.[1];
}

describe('OrderListCard: response box of the last send (CA-3)', () => {
  it('RF-06: a rejected order shows "Rejeitada", the order line in bold and the reason, above the table', () => {
    const cardHtml = renderOrderListCardAfterSend({
      outcome: 'rejected', serverMessage: 'Ordem rejeitada: a exposição de PETR4 passaria do limite de 100.000.000,00.',
      clOrdId: 'send-1', orderId: 'order-1', symbol: 'PETR4', side: 'buy', quantity: 100, priceInReais: 10, attemptedOrder: petr4BuyAttempt,
    });
    expect(cardHtml).toContain('class="order-response-box order-response-box-rejected" role="status" data-testid="caixa-de-resposta"');
    expect(readResponseBoxText(cardHtml, 'status-da-ordem')).toBe('Rejeitada');
    expect(readResponseBoxText(cardHtml, 'ordem-da-resposta')).toBe('PETR4 · Compra · 100 × R$ 10,00');
    expect(readResponseBoxText(cardHtml, 'mensagem-da-ordem')).toBe('Ordem rejeitada: a exposição de PETR4 passaria do limite de 100.000.000,00.');
    expect(cardHtml.indexOf('data-testid="caixa-de-resposta"')).toBeLessThan(cardHtml.indexOf('<table'));
  });

  it('RF-06: an accepted order also gets the box, with "Aceita"', () => {
    const cardHtml = renderOrderListCardAfterSend({
      outcome: 'accepted', serverMessage: 'Ordem aceita.', clOrdId: 'send-2', orderId: 'order-2',
      symbol: 'VALE3', side: 'sell', quantity: 5, priceInReais: 60.25,
      attemptedOrder: { symbol: 'VALE3', side: 'sell', quantity: 5, priceInReais: 60.25 },
    });
    expect(cardHtml).toContain('class="order-response-box order-response-box-accepted" role="status" data-testid="caixa-de-resposta"');
    expect(readResponseBoxText(cardHtml, 'status-da-ordem')).toBe('Aceita');
    expect(readResponseBoxText(cardHtml, 'ordem-da-resposta')).toBe('VALE3 · Venda · 5 × R$ 60,25');
    expect(readResponseBoxText(cardHtml, 'mensagem-da-ordem')).toBe('Ordem aceita.');
  });

  it('RF-07 and CA-26: the 400 shows "Não enviada", the typed text of the test mode and each field error', () => {
    const cardHtml = renderOrderListCardAfterSend({
      outcome: 'invalid', serverMessage: 'A ordem tem campos inválidos.', fieldErrors: ['A quantidade deve ser um número inteiro.'],
      attemptedOrder: { symbol: 'PETR4', side: 'buy', quantity: 'abc', priceInReais: '10,00' },
    });
    expect(cardHtml).toContain('class="order-response-box order-response-box-invalid" role="alert" data-testid="faixa-da-falha-no-envio"');
    expect(readResponseBoxText(cardHtml, 'status-da-ordem')).toBe('Não enviada');
    expect(readResponseBoxText(cardHtml, 'ordem-da-resposta')).toBe('PETR4 · Compra · abc × R$ 10,00');
    expect(cardHtml).toContain('<ul class="order-response-box-errors" data-testid="erros-de-campo-da-ordem"><li>A quantidade deve ser um número inteiro.</li></ul>');
  });

  it('RF-08: the 422 shows "Não entrou" with the reject text', () => {
    const cardHtml = renderOrderListCardAfterSend({ outcome: 'not-entered', serverMessage: 'Required tag missing.', attemptedOrder: petr4BuyAttempt });
    expect(cardHtml).toContain('class="order-response-box order-response-box-not-entered" role="alert" data-testid="faixa-da-falha-no-envio"');
    expect(readResponseBoxText(cardHtml, 'status-da-ordem')).toBe('Não entrou');
    expect(readResponseBoxText(cardHtml, 'ordem-da-resposta')).toBe('PETR4 · Compra · 100 × R$ 10,00');
    expect(readResponseBoxText(cardHtml, 'mensagem-da-ordem')).toBe('Required tag missing.');
  });

  it('RF-10: no answer in time shows "Sem confirmação" and the "may have been accepted" warning', () => {
    const cardHtml = renderOrderListCardAfterSend({
      outcome: 'maybe-accepted', serverMessage: 'A ordem pode ter sido aceita. Confira a lista antes de enviar de novo.', attemptedOrder: petr4BuyAttempt,
    });
    expect(cardHtml).toContain('class="order-response-box order-response-box-maybe-accepted" role="alert" data-testid="faixa-da-falha-no-envio"');
    expect(readResponseBoxText(cardHtml, 'status-da-ordem')).toBe('Sem confirmação');
    expect(readResponseBoxText(cardHtml, 'ordem-da-resposta')).toBe('PETR4 · Compra · 100 × R$ 10,00');
    expect(readResponseBoxText(cardHtml, 'mensagem-da-ordem')).toBe('A ordem pode ter sido aceita. Confira a lista antes de enviar de novo.');
  });

  it('RF-09: no communication shows "Erro de comunicação"', () => {
    const cardHtml = renderOrderListCardAfterSend({
      outcome: 'communication-failure', serverMessage: 'A ordem não foi confirmada: o servidor de ordens não respondeu. Tente de novo em instantes.',
      attemptedOrder: petr4BuyAttempt,
    });
    expect(readResponseBoxText(cardHtml, 'status-da-ordem')).toBe('Erro de comunicação');
    expect(readResponseBoxText(cardHtml, 'ordem-da-resposta')).toBe('PETR4 · Compra · 100 × R$ 10,00');
    expect(readResponseBoxText(cardHtml, 'mensagem-da-ordem')).toBe('A ordem não foi confirmada: o servidor de ordens não respondeu. Tente de novo em instantes.');
    expect(cardHtml).toContain('class="order-response-box order-response-box-communication-failure" role="alert" data-testid="faixa-da-falha-no-envio"');
  });

  it('RF-04: before any send there is no box', () => {
    const cardHtml = renderOrderListCard(ORDER_LIST_STATES[3][1]);
    expect(cardHtml).not.toContain('order-response-box');
  });
});
