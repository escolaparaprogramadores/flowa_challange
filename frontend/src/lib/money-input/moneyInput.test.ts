import { describe, expect, it } from 'vitest';
import {
  formatPriceInCentsForInput,
  formatTestModeReais,
  readTestModeNumberText,
  readTypedPriceInCents,
  readTypedQuantityDigits,
} from './moneyInput';

function typePriceKeyByKey(typedPriceKeys: string[]): string {
  let shownPriceInCents = 0;
  for (const typedPriceKey of typedPriceKeys) {
    shownPriceInCents = readTypedPriceInCents(formatPriceInCentsForInput(shownPriceInCents) + typedPriceKey, shownPriceInCents);
  }
  return formatPriceInCentsForInput(shownPriceInCents);
}

function typeQuantityKeyByKey(typedQuantityKeys: string): string {
  let shownQuantityDigits = '';
  for (const typedQuantityKey of typedQuantityKeys) {
    shownQuantityDigits = readTypedQuantityDigits(shownQuantityDigits + typedQuantityKey, shownQuantityDigits);
  }
  return shownQuantityDigits;
}

describe('price mask', () => {
  it('starts showing 0,00', () => {
    expect(formatPriceInCentsForInput(0)).toBe('0,00');
  });

  it.each([
    [['2'], '0,02'],
    [['2', '5'], '0,25'],
    [['2', '5', '0'], '2,50'],
    [['2', '5', '0', '0'], '25,00'],
  ])('typing %j fills from the right and shows %s', (typedPriceKeys, expectedShownPrice) => {
    expect(typePriceKeyByKey(typedPriceKeys)).toBe(expectedShownPrice);
  });

  it('stops at 999,99 and refuses the next digit', () => {
    expect(typePriceKeyByKey(['9', '9', '9', '9', '9', '9'])).toBe('999,99');
    expect(typePriceKeyByKey(['9', '9', '9', '9', '9', '9', '9'])).toBe('999,99');
  });

  it('deleting the last digit moves the cents back to the right', () => {
    expect(formatPriceInCentsForInput(readTypedPriceInCents('25,0', 2500))).toBe('2,50');
  });

  it('deleting everything goes back to 0,00', () => {
    expect(formatPriceInCentsForInput(readTypedPriceInCents('', 2500))).toBe('0,00');
  });

  it.each([
    ['10,00', 1000],
    ['12,34', 1234],
    ['950,00', 95000],
    ['999,99', 99999],
  ])('pasting %s reads %i cents', (pastedPriceText, expectedPriceInCents) => {
    expect(readTypedPriceInCents(pastedPriceText, 0)).toBe(expectedPriceInCents);
  });

  it('pasting a price above 999,99 keeps the previous price', () => {
    expect(readTypedPriceInCents('1.000,00', 2500)).toBe(2500);
  });
});

describe('quantity limit', () => {
  it('typing 9999999 key by key stops at 99999', () => {
    expect(typeQuantityKeyByKey('9999999')).toBe('99999');
  });

  it.each([
    ['99.999', '99999'],
    ['1.000', '1000'],
    ['abc', ''],
    ['1,5', '15'],
    ['-3', '3'],
  ])('only digits of %s get into the field: %s', (typedQuantityText, expectedQuantityDigits) => {
    expect(readTypedQuantityDigits(typedQuantityText, '100')).toBe(expectedQuantityDigits);
  });

  it('a change above 99999 keeps the previous quantity', () => {
    expect(readTypedQuantityDigits('100000', '10000')).toBe('10000');
  });
});

describe('test mode numbers', () => {
  it.each([
    ['10,005', 10.005],
    ['1,5', 1.5],
    ['25', 25],
    ['-2', -2],
  ])('reads %s as %d', (typedTestModeText, expectedNumber) => {
    expect(readTestModeNumberText(typedTestModeText)).toBe(expectedNumber);
  });

  it.each([['abc'], [''], ['1.5'], ['1,2,3']])('does not read %j', (typedTestModeText) => {
    expect(readTestModeNumberText(typedTestModeText)).toBeUndefined();
  });

  it('shows reais without rounding', () => {
    expect(formatTestModeReais(10.005).replace(/\s/g, ' ')).toBe('R$ 10,005');
    expect(formatTestModeReais(1.5 * 10.005).replace(/\s/g, ' ')).toBe('R$ 15,0075');
    expect(formatTestModeReais(25).replace(/\s/g, ' ')).toBe('R$ 25,00');
  });
});
