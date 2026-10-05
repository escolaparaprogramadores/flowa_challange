import { describe, expect, it } from 'vitest';
import { formatBrazilianReais, formatOrderQuantity, parseTypedWholeQuantity, validateOrderPrice, validateOrderQuantity } from './orderValidation';

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

describe('formatting for the screen', () => {
  it('formats reais with decimal comma and thousands dot', () => {
    expect(formatBrazilianReais(10_000)).toBe('R$ 10.000,00');
  });

  it('formats quantity with thousands dot', () => {
    expect(formatOrderQuantity(99_999)).toBe('99.999');
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
