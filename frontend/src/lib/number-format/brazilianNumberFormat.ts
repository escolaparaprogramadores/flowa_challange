const brazilianReaisFormatter = new Intl.NumberFormat('pt-BR', { style: 'currency', currency: 'BRL' });
const brazilianWholeNumberFormatter = new Intl.NumberFormat('pt-BR', { maximumFractionDigits: 0 });

export function formatBrazilianReais(amountInReais: number): string {
  return brazilianReaisFormatter.format(amountInReais);
}

export function formatBrazilianWholeNumber(wholeNumber: number): string {
  return brazilianWholeNumberFormatter.format(wholeNumber);
}
