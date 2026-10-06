const brazilianReaisFormatter = new Intl.NumberFormat('pt-BR', { style: 'currency', currency: 'BRL' });
const brazilianWholeNumberFormatter = new Intl.NumberFormat('pt-BR', { maximumFractionDigits: 0 });
const MINIMUM_ORDER_PRICE_DECIMALS = 2;
const MAXIMUM_ORDER_PRICE_DECIMALS = 20;

export function formatBrazilianReais(amountInReais: number): string {
  return brazilianReaisFormatter.format(amountInReais);
}

export function formatBrazilianWholeNumber(wholeNumber: number): string {
  return brazilianWholeNumberFormatter.format(wholeNumber);
}

// The order price is shown as it was sent: 10.005 stays "R$ 10,005", never rounded to "R$ 10,01".
export function formatBrazilianOrderPrice(priceInReais: number): string {
  const priceDecimals = Math.min(
    Math.max(countDecimalPlacesOfPrice(priceInReais), MINIMUM_ORDER_PRICE_DECIMALS),
    MAXIMUM_ORDER_PRICE_DECIMALS,
  );
  return new Intl.NumberFormat('pt-BR', {
    style: 'currency',
    currency: 'BRL',
    minimumFractionDigits: priceDecimals,
    maximumFractionDigits: priceDecimals,
  }).format(priceInReais);
}

// Counts from the shortest text of the number (String), not from its binary value: 10.005 is stored as
// 10.00499999..., and asking Intl for "all digits" would print that instead of 10,005.
function countDecimalPlacesOfPrice(priceInReais: number): number {
  if (!Number.isFinite(priceInReais)) return 0;
  const [priceMantissaText, priceExponentText] = String(Math.abs(priceInReais)).split('e');
  const mantissaDecimalPlaces = priceMantissaText.split('.')[1]?.length ?? 0;
  return Math.max(mantissaDecimalPlaces - Number(priceExponentText ?? 0), 0);
}
