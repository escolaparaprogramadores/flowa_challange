export const MAX_TYPED_PRICE_IN_CENTS = 99_999;
export const MAX_TYPED_QUANTITY = 99_999;

const NON_DIGIT_CHARACTERS = /\D/g;

// Bank-style mask: every digit of the new text is read as cents, so typing
// 2, 5, 0, 0 fills from the right (0,02 → 0,25 → 2,50 → 25,00). A change that would
// pass the maximum is refused and the previous price stays.
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
  maximumFractionDigits: 20,
});

export function formatTestModeReais(amountInReais: number): string {
  return testModeReaisFormatter.format(amountInReais);
}
