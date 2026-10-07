import { MAX_PRICE_EXCLUSIVE_IN_CENTS, MAX_QUANTITY_EXCLUSIVE } from '../order-validation/orderValidation';

export const MAX_TYPED_PRICE_IN_CENTS = MAX_PRICE_EXCLUSIVE_IN_CENTS - 1;
export const MAX_TYPED_QUANTITY = MAX_QUANTITY_EXCLUSIVE - 1;

const NON_DIGIT_CHARACTERS = /\D/g;

// Reading every digit of the new text, and not only the key just pressed, keeps paste and
// fill('10,00') working with the bank-style mask.
export function readTypedPriceInCents(typedPriceText: string, previousPriceInCents: number): number {
  const typedPriceDigits = typedPriceText.replace(NON_DIGIT_CHARACTERS, '');
  if (typedPriceDigits === '') return 0;
  const typedPriceInCents = Number(typedPriceDigits);
  return typedPriceInCents > MAX_TYPED_PRICE_IN_CENTS ? previousPriceInCents : typedPriceInCents;
}

export function formatPriceInCentsForInput(priceInCents: number): string {
  const wholeReais = Math.floor(priceInCents / 100);
  const remainingCents = String(priceInCents % 100).padStart(2, '0');
  return `${wholeReais},${remainingCents}`;
}

export function readTypedQuantityDigits(typedQuantityText: string, previousQuantityDigits: string): string {
  const typedQuantityDigits = typedQuantityText.replace(NON_DIGIT_CHARACTERS, '');
  if (typedQuantityDigits === '') return '';
  return Number(typedQuantityDigits) > MAX_TYPED_QUANTITY ? previousQuantityDigits : typedQuantityDigits;
}

// Test mode shows the typed number without rounding ("10,005" stays 10.005), so the
// estimate matches what is sent. Only a plain number with an optional decimal comma is read.
export function readTestModeNumberText(typedTestModeText: string): number | undefined {
  const trimmedTestModeText = typedTestModeText.trim();
  if (!/^-?\d+(,\d+)?$/.test(trimmedTestModeText)) return undefined;
  return Number(trimmedTestModeText.replace(',', '.'));
}

const testModeReaisFormatter = new Intl.NumberFormat('pt-BR', {
  style: 'currency',
  currency: 'BRL',
  minimumFractionDigits: 2,
  // Eight places keep 10,005 and 15,0075 whole and hide float noise such as 0,30000000000000004.
  maximumFractionDigits: 8,
});

export function formatTestModeReais(amountInReais: number): string {
  return testModeReaisFormatter.format(amountInReais);
}
