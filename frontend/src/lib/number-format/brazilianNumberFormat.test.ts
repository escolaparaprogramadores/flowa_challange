import { describe, expect, it } from 'vitest';
import { formatBrazilianReais, formatBrazilianWholeNumber } from './brazilianNumberFormat';

describe('formatting for the screen', () => {
  it('formats reais with decimal comma and thousands dot', () => {
    expect(formatBrazilianReais(10_000)).toBe('R$ 10.000,00');
  });

  it.each([
    [99_999, '99.999'],
    [1_000, '1.000'],
    [7, '7'],
  ])('formats the whole number %i as %s, with thousands dot', (wholeNumber, expectedText) => {
    expect(formatBrazilianWholeNumber(wholeNumber)).toBe(expectedText);
  });
});
