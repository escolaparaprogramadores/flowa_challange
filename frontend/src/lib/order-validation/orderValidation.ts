export const ORDER_TICKET_SYMBOLS = ['PETR4', 'VALE3', 'VIIA4'] as const;
export type OrderTicketSymbol = (typeof ORDER_TICKET_SYMBOLS)[number];
export type OrderSide = 'buy' | 'sell';

export const MAX_QUANTITY_EXCLUSIVE = 100_000;
const MAX_PRICE_EXCLUSIVE_IN_CENTS = 100_000;

// Brazilian format: a comma separates the cents and a dot only shows up as the
// thousands separator in groups of three ("1.000" is one thousand, not one point zero).
const BRAZILIAN_NUMBER_FORMAT = /^-?(\d{1,3}(\.\d{3})+|\d+)(,\d+)?$/;

type BrazilianNumberReading =
  | { wasRead: true; integerDigits: string; decimalDigits: string; isNegative: boolean }
  | { wasRead: false; refusalReason: 'empty' | 'format' | 'decimal-point' };

function readBrazilianNumber(typedText: string): BrazilianNumberReading {
  const trimmedText = typedText.trim();
  if (trimmedText === '') return { wasRead: false, refusalReason: 'empty' };
  if (!BRAZILIAN_NUMBER_FORMAT.test(trimmedText)) {
    const usedDotAsDecimalSeparator = /^-?\d+\.\d+$/.test(trimmedText);
    return { wasRead: false, refusalReason: usedDotAsDecimalSeparator ? 'decimal-point' : 'format' };
  }
  const typedNumberIsNegative = trimmedText.startsWith('-');
  const [integerDigitsWithDots, decimalDigits = ''] = trimmedText.replace('-', '').split(',');
  return { wasRead: true, integerDigits: integerDigitsWithDots.replaceAll('.', ''), decimalDigits, isNegative: typedNumberIsNegative };
}

export type QuantityValidation =
  | { acceptedQuantity: number; errorMessage?: undefined }
  | { acceptedQuantity?: undefined; errorMessage: string };

// Reads the typed whole quantity without checking the range; the − and + step starts from it.
export function parseTypedWholeQuantity(typedQuantity: string): number | undefined {
  const quantityReading = readBrazilianNumber(typedQuantity);
  if (!quantityReading.wasRead || quantityReading.decimalDigits !== '') return undefined;
  return Number(quantityReading.integerDigits) * (quantityReading.isNegative ? -1 : 1);
}

export function validateOrderQuantity(typedQuantity: string): QuantityValidation {
  const quantityReading = readBrazilianNumber(typedQuantity);
  if (!quantityReading.wasRead) {
    if (quantityReading.refusalReason === 'empty') return { errorMessage: 'Informe a quantidade.' };
    return { errorMessage: 'A quantidade deve ser um número inteiro.' };
  }
  if (quantityReading.decimalDigits !== '') return { errorMessage: 'A quantidade deve ser um número inteiro.' };
  const validatedOrderQuantity = Number(quantityReading.integerDigits) * (quantityReading.isNegative ? -1 : 1);
  if (validatedOrderQuantity <= 0) return { errorMessage: 'A quantidade deve ser maior que zero.' };
  if (validatedOrderQuantity >= MAX_QUANTITY_EXCLUSIVE) return { errorMessage: 'A quantidade deve ser menor que 100.000.' };
  return { acceptedQuantity: validatedOrderQuantity };
}

export type PriceValidation =
  | { acceptedPriceInCents: number; errorMessage?: undefined }
  | { acceptedPriceInCents?: undefined; errorMessage: string };

// The price becomes whole cents so "multiple of 0.01" does not depend on
// floating point rounding.
export function validateOrderPrice(typedPrice: string): PriceValidation {
  const priceReading = readBrazilianNumber(typedPrice);
  if (!priceReading.wasRead) {
    if (priceReading.refusalReason === 'empty') return { errorMessage: 'Informe o preço.' };
    if (priceReading.refusalReason === 'decimal-point') return { errorMessage: 'Use vírgula para os centavos (ex.: 10,50).' };
    return { errorMessage: 'O preço deve ser um número.' };
  }
  const centsWithoutTrailingZeros = priceReading.decimalDigits.replace(/0+$/, '');
  if (centsWithoutTrailingZeros.length > 2) return { errorMessage: 'O preço deve ser múltiplo de 0,01.' };
  const priceInCents =
    (Number(priceReading.integerDigits) * 100 + Number(centsWithoutTrailingZeros.padEnd(2, '0'))) * (priceReading.isNegative ? -1 : 1);
  if (priceInCents <= 0) return { errorMessage: 'O preço deve ser maior que zero.' };
  if (priceInCents >= MAX_PRICE_EXCLUSIVE_IN_CENTS) return { errorMessage: 'O preço deve ser menor que 1.000,00.' };
  return { acceptedPriceInCents: priceInCents };
}

const brazilianReaisFormatter = new Intl.NumberFormat('pt-BR', { style: 'currency', currency: 'BRL' });
const orderQuantityFormatter = new Intl.NumberFormat('pt-BR', { maximumFractionDigits: 0 });

export function formatBrazilianReais(amountInReais: number): string {
  return brazilianReaisFormatter.format(amountInReais);
}

export function formatOrderQuantity(shareQuantity: number): string {
  return orderQuantityFormatter.format(shareQuantity);
}
