export const SIMBOLOS_DA_BOLETA = ['PETR4', 'VALE3', 'VIIA4'] as const;
export type SimboloDaBoleta = (typeof SIMBOLOS_DA_BOLETA)[number];
export type LadoDaOrdem = 'Compra' | 'Venda';

export const QUANTIDADE_MAXIMA_EXCLUSIVA = 100_000;
const PRECO_MAXIMO_EXCLUSIVO_EM_CENTAVOS = 100_000;

// Formato brasileiro: vírgula separa os centavos e ponto só aparece como
// separador de milhar em grupos de três ("1.000" é mil, não um vírgula zero).
const NUMERO_NO_FORMATO_BRASILEIRO = /^-?(\d{1,3}(\.\d{3})+|\d+)(,\d+)?$/;

type LeituraDoNumeroBrasileiro =
  | { lido: true; parteInteira: string; casasDecimais: string; negativo: boolean }
  | { lido: false; motivoDaRecusa: 'vazio' | 'formato' | 'ponto-decimal' };

function lerNumeroBrasileiro(textoDigitado: string): LeituraDoNumeroBrasileiro {
  const textoSemEspacos = textoDigitado.trim();
  if (textoSemEspacos === '') return { lido: false, motivoDaRecusa: 'vazio' };
  if (!NUMERO_NO_FORMATO_BRASILEIRO.test(textoSemEspacos)) {
    const usouPontoComoDecimal = /^-?\d+\.\d+$/.test(textoSemEspacos);
    return { lido: false, motivoDaRecusa: usouPontoComoDecimal ? 'ponto-decimal' : 'formato' };
  }
  const negativo = textoSemEspacos.startsWith('-');
  const [parteInteiraComPontos, casasDecimais = ''] = textoSemEspacos.replace('-', '').split(',');
  return { lido: true, parteInteira: parteInteiraComPontos.replaceAll('.', ''), casasDecimais, negativo };
}

export type ValidacaoDaQuantidade =
  | { quantidadeAceita: number; mensagemDeErro?: undefined }
  | { quantidadeAceita?: undefined; mensagemDeErro: string };

export function validarQuantidade(quantidadeDigitada: string): ValidacaoDaQuantidade {
  const leitura = lerNumeroBrasileiro(quantidadeDigitada);
  if (!leitura.lido) {
    if (leitura.motivoDaRecusa === 'vazio') return { mensagemDeErro: 'Informe a quantidade.' };
    return { mensagemDeErro: 'A quantidade deve ser um número inteiro.' };
  }
  if (leitura.casasDecimais !== '') return { mensagemDeErro: 'A quantidade deve ser um número inteiro.' };
  const quantidade = Number(leitura.parteInteira) * (leitura.negativo ? -1 : 1);
  if (quantidade <= 0) return { mensagemDeErro: 'A quantidade deve ser maior que zero.' };
  if (quantidade >= QUANTIDADE_MAXIMA_EXCLUSIVA) return { mensagemDeErro: 'A quantidade deve ser menor que 100.000.' };
  return { quantidadeAceita: quantidade };
}

export type ValidacaoDoPreco =
  | { precoAceitoEmCentavos: number; mensagemDeErro?: undefined }
  | { precoAceitoEmCentavos?: undefined; mensagemDeErro: string };

// O preço vira centavos inteiros para "múltiplo de 0,01" não depender de
// arredondamento de ponto flutuante.
export function validarPreco(precoDigitado: string): ValidacaoDoPreco {
  const leitura = lerNumeroBrasileiro(precoDigitado);
  if (!leitura.lido) {
    if (leitura.motivoDaRecusa === 'vazio') return { mensagemDeErro: 'Informe o preço.' };
    if (leitura.motivoDaRecusa === 'ponto-decimal') return { mensagemDeErro: 'Use vírgula para os centavos (ex.: 10,50).' };
    return { mensagemDeErro: 'O preço deve ser um número.' };
  }
  const centavosSemZerosADireita = leitura.casasDecimais.replace(/0+$/, '');
  if (centavosSemZerosADireita.length > 2) return { mensagemDeErro: 'O preço deve ser múltiplo de 0,01.' };
  const precoEmCentavos =
    (Number(leitura.parteInteira) * 100 + Number(centavosSemZerosADireita.padEnd(2, '0'))) * (leitura.negativo ? -1 : 1);
  if (precoEmCentavos <= 0) return { mensagemDeErro: 'O preço deve ser maior que zero.' };
  if (precoEmCentavos >= PRECO_MAXIMO_EXCLUSIVO_EM_CENTAVOS) return { mensagemDeErro: 'O preço deve ser menor que 1.000,00.' };
  return { precoAceitoEmCentavos: precoEmCentavos };
}

const formatadorDeReais = new Intl.NumberFormat('pt-BR', { style: 'currency', currency: 'BRL' });
const formatadorDeQuantidade = new Intl.NumberFormat('pt-BR', { maximumFractionDigits: 0 });

export function formatarReais(valorEmReais: number): string {
  return formatadorDeReais.format(valorEmReais);
}

export function formatarQuantidade(quantidade: number): string {
  return formatadorDeQuantidade.format(quantidade);
}
