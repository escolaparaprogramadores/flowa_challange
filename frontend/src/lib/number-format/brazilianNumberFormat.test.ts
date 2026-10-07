import { describe, expect, it } from 'vitest';
import { formatBrazilianOrderPrice, formatBrazilianOrderQuantity, formatBrazilianReais, formatBrazilianWholeNumber } from './brazilianNumberFormat';

describe('formatting for the screen', () => {
  it('formats reais with decimal comma and thousands dot', () => {
    expect(formatBrazilianReais(10_000)).toBe('R$ 10.000,00');
  });

  // Exposure and the ticket summary keep two decimals: the order price rule must not leak into them.
  it.each([
    [10.005, 'R$ 10,01'],
    [99_999_999.99, 'R$ 99.999.999,99'],
    [-39_999_535.62, '-R$ 39.999.535,62'],
  ])('keeps formatting the amount %d in reais with two decimals as %s', (amountInReais, expectedText) => {
    expect(formatBrazilianReais(amountInReais)).toBe(expectedText);
  });

  it.each([
    [99_999, '99.999'],
    [1_000, '1.000'],
    [7, '7'],
  ])('formats the whole number %i as %s, with thousands dot', (wholeNumber, expectedText) => {
    expect(formatBrazilianWholeNumber(wholeNumber)).toBe(expectedText);
  });
});

describe('order price as it was sent', () => {
  it.each([
    [10.005, 'R$ 10,005'],
    [10, 'R$ 10,00'],
    [3.33, 'R$ 3,33'],
    [7.5, 'R$ 7,50'],
    [999.99, 'R$ 999,99'],
    [12_345.6789, 'R$ 12.345,6789'],
    [0.00000001, 'R$ 0,00000001'],
  ])('formats the order price %d as %s', (priceInReais, expectedText) => {
    expect(formatBrazilianOrderPrice(priceInReais)).toBe(expectedText);
  });
});

describe('order quantity as it was stored', () => {
  it.each([
    [1.5, '1,5'],
    [99_999, '99.999'],
    [100, '100'],
    [12_345.25, '12.345,25'],
  ])('formats the order quantity %d as %s, never rounding it', (orderQuantity, expectedText) => {
    expect(formatBrazilianOrderQuantity(orderQuantity)).toBe(expectedText);
  });
});
