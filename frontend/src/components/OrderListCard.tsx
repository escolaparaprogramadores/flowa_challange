import type { ReactNode } from 'react';
import { AlertIcon, DocumentIcon } from './Icons';
import type { OrderListState, OrderSendFailure } from '../hooks/useOrdersAndExposures';
import { formatInstantInBrasiliaTime } from '../lib/brasilia-time/brasiliaTime';
import { formatBrazilianReais, formatBrazilianWholeNumber } from '../lib/number-format/brazilianNumberFormat';
import type { ListedOrder } from '../services/ordersService';

type OrderListCardProps = {
  orderListState: OrderListState;
  isSendingOrder: boolean;
  lastSendFailure?: OrderSendFailure;
  headerAction?: ReactNode;
  footer?: ReactNode;
};

const SEND_FAILURE_LABELS = { invalid: 'Não enviada', 'communication-failure': 'Erro de comunicação' } as const;
const STORED_ORDER_OUTCOME_LABELS = { accepted: 'Aceita', rejected: 'Rejeitada' } as const;
const ORDER_SIDE_LABELS = { buy: 'Compra', sell: 'Venda' } as const;

export function OrderListCard({ orderListState, isSendingOrder, lastSendFailure, headerAction, footer }: OrderListCardProps) {
  const hasOrdersOnPage = orderListState.status === 'ready' && orderListState.orderListPage.orders.length > 0;
  return (
    <section className="card response order-list" aria-labelledby="order-list-title" aria-busy={orderListState.status === 'loading'}>
      <div className="order-list-header">
        <h2 className="card-title" id="order-list-title">Compra/Venda</h2>
        {isSendingOrder && <span className="order-badge order-badge-sending" role="status" data-testid="selo-enviando">Enviando…</span>}
        {headerAction}
      </div>
      {lastSendFailure && <SendFailureBanner sendFailure={lastSendFailure} />}
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

function SendFailureBanner({ sendFailure }: { sendFailure: OrderSendFailure }) {
  return (
    <div className="send-failure-banner" role="alert" data-testid="faixa-da-falha-no-envio">
      <AlertIcon className="send-failure-banner-icon" />
      <div>
        <p className="send-failure-banner-title" data-testid="status-da-ordem">{SEND_FAILURE_LABELS[sendFailure.outcome]}</p>
        <p className="send-failure-banner-message" data-testid="mensagem-da-ordem">{sendFailure.serverMessage}</p>
        {sendFailure.outcome === 'invalid' && sendFailure.fieldErrors.length > 0 && (
          <ul className="send-failure-banner-errors" data-testid="erros-de-campo-da-ordem">
            {sendFailure.fieldErrors.map((fieldErrorMessage) => (
              <li key={fieldErrorMessage}>{fieldErrorMessage}</li>
            ))}
          </ul>
        )}
      </div>
    </div>
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
      <td data-column="asset" data-label="Ativo">{listedOrder.symbol ?? '—'}</td>
      <td data-column="side" data-label="Lado">{listedOrder.side ? ORDER_SIDE_LABELS[listedOrder.side] : '—'}</td>
      <td data-column="quantity" data-label="Quantidade" className="numeric">{formatBrazilianWholeNumber(listedOrder.quantity)}</td>
      <td data-column="price" data-label="Preço" className="numeric">{formatBrazilianReais(listedOrder.priceInReais)}</td>
      <td data-column="order-number" data-label="Número da ordem" className="numeric order-row-code">{listedOrder.orderId}</td>
      <td data-column="send-identifier" data-label="Identificador do envio" className="numeric order-row-code">{listedOrder.clOrdId}</td>
    </tr>
  );
}
