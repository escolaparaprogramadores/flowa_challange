import { describe, expect, it } from 'vitest';
import { formatarQuantidade, formatarReais, validarPreco, validarQuantidade } from './validacaoDaOrdem';

describe('validarQuantidade', () => {
  it.each([
    ['1', 1],
    ['99.999', 99_999],
    ['99999', 99_999],
    [' 250 ', 250],
  ])('aceita %s', (quantidadeDigitada, quantidadeEsperada) => {
    expect(validarQuantidade(quantidadeDigitada)).toEqual({ quantidadeAceita: quantidadeEsperada });
  });

  it.each([
    ['', 'Informe a quantidade.'],
    ['0', 'A quantidade deve ser maior que zero.'],
    ['-1', 'A quantidade deve ser maior que zero.'],
    ['1,5', 'A quantidade deve ser um número inteiro.'],
    ['1.5', 'A quantidade deve ser um número inteiro.'],
    ['abc', 'A quantidade deve ser um número inteiro.'],
    ['100.000', 'A quantidade deve ser menor que 100.000.'],
    ['100000', 'A quantidade deve ser menor que 100.000.'],
  ])('recusa %s', (quantidadeDigitada, mensagemEsperada) => {
    expect(validarQuantidade(quantidadeDigitada)).toEqual({ mensagemDeErro: mensagemEsperada });
  });
});

describe('validarPreco', () => {
  it.each([
    ['0,01', 1],
    ['999,99', 99_999],
    ['10', 1_000],
    ['10,5', 1_050],
    ['10,500', 1_050],
  ])('aceita %s', (precoDigitado, centavosEsperados) => {
    expect(validarPreco(precoDigitado)).toEqual({ precoAceitoEmCentavos: centavosEsperados });
  });

  it.each([
    ['', 'Informe o preço.'],
    ['0', 'O preço deve ser maior que zero.'],
    ['-1', 'O preço deve ser maior que zero.'],
    ['1.000', 'O preço deve ser menor que 1.000,00.'],
    ['1000', 'O preço deve ser menor que 1.000,00.'],
    ['10,005', 'O preço deve ser múltiplo de 0,01.'],
    ['10.50', 'Use vírgula para os centavos (ex.: 10,50).'],
    ['abc', 'O preço deve ser um número.'],
  ])('recusa %s', (precoDigitado, mensagemEsperada) => {
    expect(validarPreco(precoDigitado)).toEqual({ mensagemDeErro: mensagemEsperada });
  });
});

describe('formatação para a tela', () => {
  it('formata reais com vírgula decimal e ponto de milhar', () => {
    expect(formatarReais(10_000)).toBe('R$ 10.000,00');
  });

  it('formata quantidade com ponto de milhar', () => {
    expect(formatarQuantidade(99_999)).toBe('99.999');
  });
});
