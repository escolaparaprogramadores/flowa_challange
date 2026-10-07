import { formatBrazilianOrderPrice, formatBrazilianWholeNumber } from '../lib/number-format/brazilianNumberFormat';
import type { AttemptedOrder, OrderSendResult } from '../services/ordersService';

const SEND_OUTCOME_LABELS: Record<OrderSendResult['outcome'], string> = {
  accepted: 'Aceita',
  rejected: 'Rejeitada',
  invalid: 'Não enviada',
  'not-entered': 'Não entrou',
  'maybe-accepted': 'Sem confirmação',
  'communication-failure': 'Erro de comunicação',
};
const ORDER_SIDE_LABELS = { buy: 'Compra', sell: 'Venda' } as const;

// The test mode keeps quantity and price as typed: the line shows the text, never a rounded number.
function describeOrderLine(attemptedOrder: AttemptedOrder) {
  const quantityOnLine =
    typeof attemptedOrder.quantity === 'number' ? formatBrazilianWholeNumber(attemptedOrder.quantity) : attemptedOrder.quantity;
  const priceOnLine =
    typeof attemptedOrder.priceInReais === 'number' ? formatBrazilianOrderPrice(attemptedOrder.priceInReais) : `R$ ${attemptedOrder.priceInReais}`;
  return `${attemptedOrder.symbol} · ${ORDER_SIDE_LABELS[attemptedOrder.side]} · ${quantityOnLine} × ${priceOnLine}`;
}

export function OrderResponseBox({ lastSendResult }: { lastSendResult?: OrderSendResult }) {
  if (!lastSendResult) return null;
  const { outcome } = lastSendResult;
  const isAnsweredOrder = outcome === 'accepted' || outcome === 'rejected';
  // The failure keeps the test ids of the old banner, which the list E2E of F7 still reads.
  return (
    <div
      className={`order-response-box order-response-box-${outcome}`}
      role={isAnsweredOrder ? 'status' : 'alert'}
      data-testid={isAnsweredOrder ? 'caixa-de-resposta' : 'faixa-da-falha-no-envio'}
    >
      <span className="order-badge order-response-box-badge" data-testid="status-da-ordem">
        {SEND_OUTCOME_LABELS[outcome]}
      </span>
      <div className="order-response-box-body">
        <p className="order-response-box-order" data-testid="ordem-da-resposta">
          {describeOrderLine(lastSendResult.attemptedOrder)}
        </p>
        <p className="order-response-box-message" data-testid="mensagem-da-ordem">{lastSendResult.serverMessage}</p>
        {outcome === 'invalid' && lastSendResult.fieldErrors.length > 0 && (
          <ul className="order-response-box-errors" data-testid="erros-de-campo-da-ordem">
            {lastSendResult.fieldErrors.map((fieldErrorMessage) => (
              <li key={fieldErrorMessage}>{fieldErrorMessage}</li>
            ))}
          </ul>
        )}
      </div>
    </div>
  );
}
