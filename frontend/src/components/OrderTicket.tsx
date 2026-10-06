import { useState, type FormEvent } from 'react';
import { ShieldIcon } from './Icons';
import {
  MAX_QUANTITY_EXCLUSIVE,
  ORDER_TICKET_SYMBOLS,
  parseTypedWholeQuantity,
  validateOrderPrice,
  validateOrderQuantity,
  type OrderSide,
  type OrderTicketSymbol,
} from '../lib/order-validation/orderValidation';
import {
  formatPriceInCentsForInput,
  formatTestModeReais,
  readTestModeNumberText,
  readTypedPriceInCents,
  readTypedQuantityDigits,
} from '../lib/money-input/moneyInput';
import { formatBrazilianReais } from '../lib/number-format/brazilianNumberFormat';
import type { OrderToSend } from '../services/ordersService';

type OrderTicketProps = {
  isSendingOrder: boolean;
  onSendOrder: (orderToSend: OrderToSend) => void;
};

type OrderTicketFieldErrors = { quantityError?: string; priceError?: string };

type TestModeTexts = { symbolText: string; quantityText: string; priceText: string };

const TEST_MODE_NOTICE = 'Modo de teste: a tela não confere os campos e envia como está. Duplo clique no símbolo para sair.';

export function OrderTicket({ isSendingOrder, onSendOrder }: OrderTicketProps) {
  const [orderSymbol, setOrderSymbol] = useState<OrderTicketSymbol>('PETR4');
  const [orderSide, setOrderSide] = useState<OrderSide>('buy');
  const [typedQuantityDigits, setTypedQuantityDigits] = useState('100');
  const [typedPriceInCents, setTypedPriceInCents] = useState(0);
  const [orderTicketFieldErrors, setOrderTicketFieldErrors] = useState<OrderTicketFieldErrors>({});
  // Lives only in memory: reloading the page always comes back to the normal mode.
  const [testModeTexts, setTestModeTexts] = useState<TestModeTexts | undefined>(undefined);
  const isTestModeOn = testModeTexts !== undefined;

  const shownPriceText = formatPriceInCentsForInput(typedPriceInCents);
  const quantityValidation = validateOrderQuantity(typedQuantityDigits);
  const priceValidation = validateOrderPrice(shownPriceText);
  const typedWholeQuantity = parseTypedWholeQuantity(typedQuantityDigits);
  const isQuantityAtMaximum = !isTestModeOn && (typedWholeQuantity ?? 0) >= MAX_QUANTITY_EXCLUSIVE - 1;

  const testModePrice = testModeTexts && readTestModeNumberText(testModeTexts.priceText);
  const testModeQuantity = testModeTexts && readTestModeNumberText(testModeTexts.quantityText);
  const summaryPriceText = isTestModeOn
    ? testModePrice !== undefined ? formatTestModeReais(testModePrice) : '—'
    : formatBrazilianReais(typedPriceInCents / 100);
  const summaryTotalText = isTestModeOn
    ? testModePrice !== undefined && testModeQuantity !== undefined ? formatTestModeReais(testModeQuantity * testModePrice) : '—'
    : typedWholeQuantity !== undefined ? formatBrazilianReais((typedWholeQuantity * typedPriceInCents) / 100) : '—';

  function turnOnTestMode(doubleClickedSymbol: OrderTicketSymbol) {
    setTestModeTexts({ symbolText: doubleClickedSymbol, quantityText: typedQuantityDigits, priceText: shownPriceText });
    setOrderTicketFieldErrors({});
  }

  function turnOffTestMode() {
    setTestModeTexts(undefined);
  }

  function changeTestModeText(changedTexts: Partial<TestModeTexts>) {
    setTestModeTexts((previousTestModeTexts) => previousTestModeTexts && { ...previousTestModeTexts, ...changedTexts });
  }

  function stepQuantityBy(orderQuantityStep: number) {
    if (testModeTexts) {
      changeTestModeText({ quantityText: String((parseTypedWholeQuantity(testModeTexts.quantityText) ?? 0) + orderQuantityStep) });
      return;
    }
    const nextQuantity = Math.min(Math.max((typedWholeQuantity ?? 0) + orderQuantityStep, 1), MAX_QUANTITY_EXCLUSIVE - 1);
    setTypedQuantityDigits(String(nextQuantity));
    setOrderTicketFieldErrors((previousFieldErrors) => ({ ...previousFieldErrors, quantityError: undefined }));
  }

  function submitOrderTicket(orderTicketSubmitEvent: FormEvent<HTMLFormElement>) {
    orderTicketSubmitEvent.preventDefault();
    if (isSendingOrder) return;
    if (testModeTexts) {
      onSendOrder({ mode: 'test', symbol: testModeTexts.symbolText, side: orderSide, quantityText: testModeTexts.quantityText, priceText: testModeTexts.priceText });
      return;
    }
    setOrderTicketFieldErrors({
      quantityError: quantityValidation.errorMessage,
      priceError: priceValidation.errorMessage,
    });
    if (quantityValidation.acceptedQuantity === undefined || priceValidation.acceptedPriceInCents === undefined) return;
    onSendOrder({ mode: 'normal', symbol: orderSymbol, side: orderSide, quantity: quantityValidation.acceptedQuantity, priceInCents: priceValidation.acceptedPriceInCents });
  }

  const { quantityError, priceError } = orderTicketFieldErrors;

  return (
    <form className="card order-ticket" onSubmit={submitOrderTicket} noValidate aria-label="Boleta de ordem">
      <div className="order-ticket-header">
        <h2 className="card-title">Nova ordem</h2>
        {isTestModeOn && <span className="test-mode-badge">Modo de teste</span>}
      </div>
      {isTestModeOn && <p className="test-mode-notice">{TEST_MODE_NOTICE}</p>}

      <fieldset className="side-toggle">
        <legend className="screen-reader-only">Lado da ordem</legend>
        <button type="button" className="side-toggle-option buy" aria-pressed={orderSide === 'buy'} onClick={() => setOrderSide('buy')}>
          Compra
        </button>
        <button type="button" className="side-toggle-option sell" aria-pressed={orderSide === 'sell'} onClick={() => setOrderSide('sell')}>
          Venda
        </button>
      </fieldset>

      {testModeTexts ? (
        <div className="field">
          <label className="field-label" htmlFor="test-mode-symbol">Símbolo</label>
          <input
            id="test-mode-symbol"
            className="text-input test-mode-symbol"
            autoComplete="off"
            autoFocus
            value={testModeTexts.symbolText}
            onChange={(symbolChangeEvent) => changeTestModeText({ symbolText: symbolChangeEvent.target.value })}
            onDoubleClick={turnOffTestMode}
          />
        </div>
      ) : (
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
                onDoubleClick={() => turnOnTestMode(availableSymbol)}
              >
                {availableSymbol}
              </button>
            ))}
          </div>
        </div>
      )}

      <div className="field">
        <label className="field-label" htmlFor="quantity">{isTestModeOn ? 'Quantidade' : `Quantidade de ${orderSymbol}`}</label>
        <div className={quantityError ? 'quantity-stepper invalid' : 'quantity-stepper'}>
          <button type="button" onClick={() => stepQuantityBy(-1)} aria-label="Diminuir quantidade">−</button>
          <input
            id="quantity"
            className="numeric"
            inputMode={isTestModeOn ? 'text' : 'numeric'}
            autoComplete="off"
            value={testModeTexts ? testModeTexts.quantityText : typedQuantityDigits}
            aria-invalid={quantityError ? true : undefined}
            aria-describedby={quantityError ? 'quantity-error' : undefined}
            onChange={(quantityChangeEvent) => {
              if (testModeTexts) {
                changeTestModeText({ quantityText: quantityChangeEvent.target.value });
                return;
              }
              const typedQuantityText = quantityChangeEvent.target.value;
              setTypedQuantityDigits((previousQuantityDigits) => readTypedQuantityDigits(typedQuantityText, previousQuantityDigits));
              setOrderTicketFieldErrors((previousFieldErrors) => ({ ...previousFieldErrors, quantityError: undefined }));
            }}
          />
          <button type="button" onClick={() => stepQuantityBy(1)} aria-label="Aumentar quantidade" disabled={isQuantityAtMaximum}>+</button>
        </div>
        {quantityError && <p id="quantity-error" className="field-error" role="alert">{quantityError}</p>}
      </div>

      <div className="field">
        <label className="field-label" htmlFor="price">Preço por ação (R$)</label>
        <input
          id="price"
          className="text-input numeric"
          inputMode={isTestModeOn ? 'text' : 'numeric'}
          autoComplete="off"
          placeholder="0,00"
          value={testModeTexts ? testModeTexts.priceText : shownPriceText}
          aria-invalid={priceError ? true : undefined}
          aria-describedby={priceError ? 'price-error' : undefined}
          onChange={(priceChangeEvent) => {
            if (testModeTexts) {
              changeTestModeText({ priceText: priceChangeEvent.target.value });
              return;
            }
            const typedPriceText = priceChangeEvent.target.value;
            setTypedPriceInCents((previousPriceInCents) => readTypedPriceInCents(typedPriceText, previousPriceInCents));
            setOrderTicketFieldErrors((previousFieldErrors) => ({ ...previousFieldErrors, priceError: undefined }));
          }}
        />
        {priceError && <p id="price-error" className="field-error" role="alert">{priceError}</p>}
      </div>

      <dl className="order-summary">
        <div className="order-summary-row">
          <dt>Preço por ação</dt>
          <dd className="numeric">{summaryPriceText}</dd>
        </div>
        <div className="order-summary-row">
          <dt>Valor total estimado</dt>
          <dd className="numeric order-summary-total" data-testid="total-estimado">
            {summaryTotalText}
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
