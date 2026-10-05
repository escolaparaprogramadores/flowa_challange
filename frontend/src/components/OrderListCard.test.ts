import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { OrderListCard } from './OrderListCard';
import type { OrderListState } from '../hooks/useOrdersAndExposures';
import type { ListedOrder } from '../services/ordersService';

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
