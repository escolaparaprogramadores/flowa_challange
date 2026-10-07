import { describe, expect, it } from 'vitest';
import {
  canIncreaseQuantity,
  parseTypedWholeQuantity,
  prepareOrderTicketSubmission,
  validateOrderPrice,
  validateOrderQuantity,
} from './orderValidation';

describe('validateOrderQuantity', () => {
  it.each([
    ['1', 1],
    ['99.999', 99_999],
    ['99999', 99_999],
    [' 250 ', 250],
  ])('accepts %s', (typedQuantity, expectedQuantity) => {
    expect(validateOrderQuantity(typedQuantity)).toEqual({ acceptedQuantity: expectedQuantity });
  });

  it.each([
    ['', 'Informe a quantidade.'],
    ['0', 'A quantidade deve ser maior que zero.'],
    ['-1', 'A quantidade deve ser maior que zero.'],
    ['1,5', 'A quantidade deve ser um número inteiro.'],
    ['1.5', 'A quantidade deve ser um número inteiro.'],
    ['abc', 'A quantidade deve ser um número inteiro.'],
    ['100.000', 'A quantidade deve ser menor que 100.000.'],
    ['100000', 'A quantidade deve ser menor que 100.000.'],
  ])('refuses %s', (typedQuantity, expectedMessage) => {
    expect(validateOrderQuantity(typedQuantity)).toEqual({ errorMessage: expectedMessage });
  });
});

describe('validateOrderPrice', () => {
  it.each([
    ['0,01', 1],
    ['999,99', 99_999],
    ['10', 1_000],
    ['10,5', 1_050],
    ['10,500', 1_050],
  ])('accepts %s', (typedPrice, expectedCents) => {
    expect(validateOrderPrice(typedPrice)).toEqual({ acceptedPriceInCents: expectedCents });
  });

  it.each([
    ['', 'Informe o preço.'],
    ['0', 'O preço deve ser maior que zero.'],
    ['-1', 'O preço deve ser maior que zero.'],
    ['1.000', 'O preço deve ser menor que 1.000,00.'],
    ['1000', 'O preço deve ser menor que 1.000,00.'],
    ['10,005', 'O preço deve ser múltiplo de 0,01.'],
    ['10.50', 'Use vírgula para os centavos (ex.: 10,50).'],
    ['abc', 'O preço deve ser um número.'],
  ])('refuses %s', (typedPrice, expectedMessage) => {
    expect(validateOrderPrice(typedPrice)).toEqual({ errorMessage: expectedMessage });
  });
});

describe('parseTypedWholeQuantity', () => {
  it.each([
    ['100.000', 100_000],
    ['250', 250],
    ['-3', -3],
  ])('reads %s even outside the order range', (typedQuantity, expectedQuantity) => {
    expect(parseTypedWholeQuantity(typedQuantity)).toBe(expectedQuantity);
  });

  it.each(['', 'abc', '1,5'])('does not read "%s", which is not a whole number', (typedQuantity) => {
    expect(parseTypedWholeQuantity(typedQuantity)).toBeUndefined();
  });
});

describe('canIncreaseQuantity', () => {
  it.each([
    ['99998', true],
    ['1', true],
    ['', true],
    ['99999', false],
  ])('with "%s" typed the + is enabled: %s', (typedQuantity, expectedCanIncrease) => {
    expect(canIncreaseQuantity(typedQuantity)).toBe(expectedCanIncrease);
  });
});

describe('prepareOrderTicketSubmission', () => {
  it('normal mode hands over whole quantity and price in cents', () => {
    expect(prepareOrderTicketSubmission({ mode: 'normal', symbol: 'VALE3', side: 'sell', typedQuantity: '99999', typedPrice: '25,00' })).toEqual({
      orderToSend: { mode: 'normal', symbol: 'VALE3', side: 'sell', quantity: 99_999, priceInCents: 2_500 },
    });
  });

  it('normal mode with empty quantity and 0,00 returns both field errors and nothing to send', () => {
    expect(prepareOrderTicketSubmission({ mode: 'normal', symbol: 'PETR4', side: 'buy', typedQuantity: '', typedPrice: '0,00' })).toEqual({
      fieldErrors: { quantityError: 'Informe a quantidade.', priceError: 'O preço deve ser maior que zero.' },
    });
  });

  it.each([
    ['ITUB4', '1,5', '10,005'],
    [' ITUB4 ', ' 1,5 ', ' 10,005 '],
    ['', 'abc', ''],
    ['petr4', '-3', '1.000,50'],
  ])('test mode hands over symbol %j, quantity %j and price %j exactly as typed', (symbolText, quantityText, priceText) => {
    expect(prepareOrderTicketSubmission({ mode: 'test', symbolText, side: 'buy', quantityText, priceText })).toEqual({
      orderToSend: { mode: 'test', symbol: symbolText, side: 'buy', quantityText, priceText },
    });
  });
});
