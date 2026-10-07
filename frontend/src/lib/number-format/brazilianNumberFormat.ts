const brazilianReaisFormatter = new Intl.NumberFormat('pt-BR', { style: 'currency', currency: 'BRL' });
const brazilianWholeNumberFormatter = new Intl.NumberFormat('pt-BR', { maximumFractionDigits: 0 });
const MINIMUM_ORDER_PRICE_DECIMALS = 2;
const MAXIMUM_SENT_NUMBER_DECIMALS = 20;

export function formatBrazilianReais(amountInReais: number): string {
  return brazilianReaisFormatter.format(amountInReais);
}

export function formatBrazilianWholeNumber(wholeNumber: number): string {
  return brazilianWholeNumberFormatter.format(wholeNumber);
}

// The order price is shown as it was sent: 10.005 stays "R$ 10,005", never rounded to "R$ 10,01".
export function formatBrazilianOrderPrice(priceInReais: number): string {
  const priceDecimals = Math.max(countDecimalPlacesAsSent(priceInReais), MINIMUM_ORDER_PRICE_DECIMALS);
  return new Intl.NumberFormat('pt-BR', {
    style: 'currency',
    currency: 'BRL',
    minimumFractionDigits: priceDecimals,
    maximumFractionDigits: priceDecimals,
  }).format(priceInReais);
}

// The test mode can store a quantity like 1.5: the list shows "1,5", never the rounded "2".
export function formatBrazilianOrderQuantity(quantity: number): string {
  const quantityDecimals = countDecimalPlacesAsSent(quantity);
  return new Intl.NumberFormat('pt-BR', { minimumFractionDigits: quantityDecimals, maximumFractionDigits: quantityDecimals }).format(quantity);
}

// Counts from the shortest text of the number (String), not from its binary value: 10.005 is stored as
// 10.00499999..., and asking Intl for "all digits" would print that instead of 10,005.
function countDecimalPlacesAsSent(sentNumber: number): number {
  if (!Number.isFinite(sentNumber)) return 0;
  const [sentMantissaText, sentExponentText] = String(Math.abs(sentNumber)).split('e');
  const mantissaDecimalPlaces = sentMantissaText.split('.')[1]?.length ?? 0;
  return Math.min(Math.max(mantissaDecimalPlaces - Number(sentExponentText ?? 0), 0), MAXIMUM_SENT_NUMBER_DECIMALS);
}
