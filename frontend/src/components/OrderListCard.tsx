import type { ReactNode } from 'react';
import { DocumentIcon } from './Icons';
import { OrderResponseBox } from './OrderResponseBox';
import type { OrderListState } from '../hooks/useOrdersAndExposures';
import { formatInstantInBrasiliaTime } from '../lib/brasilia-time/brasiliaTime';
import { formatBrazilianOrderPrice, formatBrazilianOrderQuantity } from '../lib/number-format/brazilianNumberFormat';
import type { ListedOrder, OrderSendResult } from '../services/ordersService';

type OrderListCardProps = {
  orderListState: OrderListState;
  isSendingOrder: boolean;
  lastSendResult?: OrderSendResult;
  headerAction?: ReactNode;
  footer?: ReactNode;
};

const STORED_ORDER_OUTCOME_LABELS = { accepted: 'Aceita', rejected: 'Rejeitada' } as const;
const ORDER_SIDE_LABELS = { buy: 'Compra', sell: 'Venda' } as const;

export function OrderListCard({ orderListState, isSendingOrder, lastSendResult, headerAction, footer }: OrderListCardProps) {
  const hasOrdersOnPage = orderListState.status === 'ready' && orderListState.orderListPage.orders.length > 0;
  return (
    <section className="card response order-list" aria-labelledby="order-list-title" aria-busy={orderListState.status === 'loading'}>
      <div className="order-list-header">
        <h2 className="card-title" id="order-list-title">Compra/Venda</h2>
        {isSendingOrder && <span className="order-badge order-badge-sending" role="status" data-testid="selo-enviando">Enviando…</span>}
        {headerAction}
      </div>
      <OrderResponseBox lastSendResult={lastSendResult} />
      {orderListState.status === 'loading' && <p className="order-list-notice">Carregando as ordens…</p>}
      {orderListState.status === 'error' && (
        <p className="order-list-notice order-list-notice-error" role="alert">{orderListState.errorMessage}</p>
      )}
      {orderListState.status === 'ready' && !hasOrdersOnPage && <EmptyOrderList />}
      {hasOrdersOnPage && <OrderTable listedOrders={orderListState.orderListPage.orders} />}
      {hasOrdersOnPage && footer}
    </section>
  );
}

function EmptyOrderList() {
  return (
    <div className="empty-order-list" data-testid="lista-de-ordens-vazia">
      <DocumentIcon className="empty-order-list-icon" />
      <p>Nenhuma ordem enviada ainda. Preencha a boleta e envie para ver a resposta aqui.</p>
    </div>
  );
}

function OrderTable({ listedOrders }: { listedOrders: ListedOrder[] }) {
  return (
    <div className="order-table-frame">
      <table className="order-table">
        <thead>
          <tr>
            <th scope="col">Data</th>
            <th scope="col">Status</th>
            <th scope="col">Motivo</th>
            <th scope="col">Ativo</th>
            <th scope="col">Lado</th>
            <th scope="col">Quantidade</th>
            <th scope="col">Preço</th>
            <th scope="col">Número da ordem</th>
            <th scope="col">Identificador do envio</th>
          </tr>
        </thead>
        <tbody>
          {listedOrders.map((listedOrder) => (
            <OrderRow key={`${listedOrder.clOrdId}-${listedOrder.receivedAt}`} listedOrder={listedOrder} />
          ))}
        </tbody>
      </table>
    </div>
  );
}

function OrderRow({ listedOrder }: { listedOrder: ListedOrder }) {
  const receivedInBrasiliaTime = formatInstantInBrasiliaTime(listedOrder.receivedAt);
  return (
    <tr data-testid="linha-da-ordem">
      <td data-column="date" data-label="Data">
        <span className="order-row-instant">
          <span className="order-row-day numeric">{receivedInBrasiliaTime.dayMonthYear}</span>
          <span className="order-row-time numeric">{receivedInBrasiliaTime.hourMinute}</span>
        </span>
      </td>
      <td data-column="status" data-label="Status">
        <span className={`order-badge order-badge-${listedOrder.outcome}`}>{STORED_ORDER_OUTCOME_LABELS[listedOrder.outcome]}</span>
      </td>
      <td data-column="reject-reason" data-label="Motivo" className="order-row-reject-reason">
        {listedOrder.rejectReason ? listedOrder.rejectReason : <span className="order-row-no-reason">—</span>}
      </td>
      <td data-column="asset" data-label="Ativo">{listedOrder.symbol ?? '—'}</td>
      <td data-column="side" data-label="Lado">{listedOrder.side ? ORDER_SIDE_LABELS[listedOrder.side] : '—'}</td>
      <td data-column="quantity" data-label="Quantidade" className="numeric">{formatBrazilianOrderQuantity(listedOrder.quantity)}</td>
      <td data-column="price" data-label="Preço" className="numeric">{formatBrazilianOrderPrice(listedOrder.priceInReais)}</td>
      <td data-column="order-number" data-label="Número da ordem" className="numeric order-row-code">{listedOrder.orderId}</td>
      <td data-column="send-identifier" data-label="Identificador do envio" className="numeric order-row-code">{listedOrder.clOrdId}</td>
    </tr>
  );
}
