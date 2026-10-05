import { useState, type FormEvent } from 'react';
import { ShieldIcon } from './Icons';
import {
  MAX_QUANTITY_EXCLUSIVE,
  ORDER_TICKET_SYMBOLS,
  formatBrazilianReais,
  parseTypedWholeQuantity,
  validateOrderPrice,
  validateOrderQuantity,
  type OrderSide,
  type OrderTicketSymbol,
} from '../lib/order-validation/orderValidation';
import type { OrderToSend } from '../services/ordersService';

type OrderTicketProps = {
  isSendingOrder: boolean;
  onSendOrder: (orderToSend: OrderToSend) => void;
};

type OrderTicketFieldErrors = { quantityError?: string; priceError?: string };

export function OrderTicket({ isSendingOrder, onSendOrder }: OrderTicketProps) {
  const [orderSymbol, setOrderSymbol] = useState<OrderTicketSymbol>('PETR4');
  const [orderSide, setOrderSide] = useState<OrderSide>('buy');
  const [typedQuantity, setTypedQuantity] = useState('100');
  const [typedPrice, setTypedPrice] = useState('');
  const [orderTicketFieldErrors, setOrderTicketFieldErrors] = useState<OrderTicketFieldErrors>({});

  const quantityValidation = validateOrderQuantity(typedQuantity);
  const priceValidation = validateOrderPrice(typedPrice);
  const acceptedQuantity = quantityValidation.acceptedQuantity;
  const acceptedPriceInCents = priceValidation.acceptedPriceInCents;
  const estimatedTotalInReais =
    acceptedQuantity !== undefined && acceptedPriceInCents !== undefined ? (acceptedQuantity * acceptedPriceInCents) / 100 : undefined;

  function stepQuantityBy(orderQuantityStep: number) {
    const currentQuantity = parseTypedWholeQuantity(typedQuantity) ?? 0;
    const nextQuantity = Math.min(Math.max(currentQuantity + orderQuantityStep, 1), MAX_QUANTITY_EXCLUSIVE - 1);
    setTypedQuantity(String(nextQuantity));
    setOrderTicketFieldErrors((previousFieldErrors) => ({ ...previousFieldErrors, quantityError: undefined }));
  }

  function submitOrderTicket(orderTicketSubmitEvent: FormEvent<HTMLFormElement>) {
    orderTicketSubmitEvent.preventDefault();
    setOrderTicketFieldErrors({
      quantityError: quantityValidation.errorMessage,
      priceError: priceValidation.errorMessage,
    });
    if (acceptedQuantity === undefined || acceptedPriceInCents === undefined || isSendingOrder) return;
    onSendOrder({ symbol: orderSymbol, side: orderSide, quantity: acceptedQuantity, priceInCents: acceptedPriceInCents });
  }

  const { quantityError, priceError } = orderTicketFieldErrors;

  return (
    <form className="card order-ticket" onSubmit={submitOrderTicket} noValidate aria-label="Boleta de ordem">
      <h2 className="card-title">Nova ordem</h2>

      <fieldset className="side-toggle">
        <legend className="screen-reader-only">Lado da ordem</legend>
        <button type="button" className="side-toggle-option buy" aria-pressed={orderSide === 'buy'} onClick={() => setOrderSide('buy')}>
          Compra
        </button>
        <button type="button" className="side-toggle-option sell" aria-pressed={orderSide === 'sell'} onClick={() => setOrderSide('sell')}>
          Venda
        </button>
      </fieldset>

      <div className="field">
        <span className="field-label" id="symbol-label">Símbolo</span>
        <div className="symbol-track" role="group" aria-labelledby="symbol-label">
          {ORDER_TICKET_SYMBOLS.map((availableSymbol) => (
            <button
              key={availableSymbol}
              type="button"
              className="symbol-option"
              aria-pressed={orderSymbol === availableSymbol}
              onClick={() => setOrderSymbol(availableSymbol)}
            >
              {availableSymbol}
            </button>
          ))}
        </div>
      </div>

      <div className="field">
        <label className="field-label" htmlFor="quantity">Quantidade de {orderSymbol}</label>
        <div className={quantityError ? 'quantity-stepper invalid' : 'quantity-stepper'}>
          <button type="button" onClick={() => stepQuantityBy(-1)} aria-label="Diminuir quantidade">−</button>
          <input
            id="quantity"
            className="numeric"
            inputMode="numeric"
            autoComplete="off"
            value={typedQuantity}
            aria-invalid={quantityError ? true : undefined}
            aria-describedby={quantityError ? 'quantity-error' : undefined}
            onChange={(quantityChangeEvent) => {
              setTypedQuantity(quantityChangeEvent.target.value);
              setOrderTicketFieldErrors((previousFieldErrors) => ({ ...previousFieldErrors, quantityError: undefined }));
            }}
          />
          <button type="button" onClick={() => stepQuantityBy(1)} aria-label="Aumentar quantidade">+</button>
        </div>
        {quantityError && <p id="quantity-error" className="field-error" role="alert">{quantityError}</p>}
      </div>

      <div className="field">
        <label className="field-label" htmlFor="price">Preço por ação (R$)</label>
        <input
          id="price"
          className="text-input numeric"
          inputMode="decimal"
          autoComplete="off"
          placeholder="0,00"
          value={typedPrice}
          aria-invalid={priceError ? true : undefined}
          aria-describedby={priceError ? 'price-error' : undefined}
          onChange={(priceChangeEvent) => {
            setTypedPrice(priceChangeEvent.target.value);
            setOrderTicketFieldErrors((previousFieldErrors) => ({ ...previousFieldErrors, priceError: undefined }));
          }}
        />
        {priceError && <p id="price-error" className="field-error" role="alert">{priceError}</p>}
      </div>

      <dl className="order-summary">
        <div className="order-summary-row">
          <dt>Preço por ação</dt>
          <dd className="numeric">{acceptedPriceInCents !== undefined ? formatBrazilianReais(acceptedPriceInCents / 100) : '—'}</dd>
        </div>
        <div className="order-summary-row">
          <dt>Valor total estimado</dt>
          <dd className="numeric order-summary-total" data-testid="total-estimado">
            {estimatedTotalInReais !== undefined ? formatBrazilianReais(estimatedTotalInReais) : '—'}
          </dd>
        </div>
      </dl>

      <button type="submit" className={`submit-order-button ${orderSide}`} disabled={isSendingOrder}>
        {isSendingOrder ? 'Enviando…' : orderSide === 'buy' ? 'Enviar ordem de compra' : 'Enviar ordem de venda'}
      </button>
      <p className="disclaimer">
        <ShieldIcon className="disclaimer-icon" />
        Ordem de demonstração: nenhuma operação real é feita.
      </p>
    </form>
  );
}
