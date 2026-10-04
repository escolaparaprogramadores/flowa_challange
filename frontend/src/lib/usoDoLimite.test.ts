import { describe, expect, it } from 'vitest';
import { calcularUsoDoLimite } from './usoDoLimite';

describe('calcularUsoDoLimite', () => {
  it.each([
    [0, '0%', 0],
    [78_991, '0,07%', 0.07],
    [30_000, '0,03%', 0.03],
    [10_000, '0,01%', 0.01],
    [12_500_000, '12,5%', 12.5],
    [50_000_000, '50%', 50],
    [89_999_999.99, '89,99%', 89.99],
    [90_000_000, '90%', 90],
    [95_000_000, '95%', 95],
    [99_997_997.01, '99,99%', 99.99],
    [99_999_999.99, '99,99%', 99.99],
    [100_000_000, '100%', 100],
  ])('exposição %d mostra %s, cortando sem arredondar para cima', (exposicaoEmReais, porcentagemEsperada, larguraEsperada) => {
    const usoDoLimite = calcularUsoDoLimite(exposicaoEmReais);
    expect(usoDoLimite.porcentagemMostrada).toBe(porcentagemEsperada);
    expect(usoDoLimite.larguraDaBarraEmPorcentagem).toBe(larguraEsperada);
  });

  it.each([
    [0.01, '< 0,01%'],
    [0.5, '< 0,01%'],
    [9_999.99, '< 0,01%'],
    [-0.5, '< 0,01%'],
    [-9_999.99, '< 0,01%'],
  ])('exposição %d, acima de zero e abaixo de 0,01%%, mostra "< 0,01%%"', (exposicaoEmReais, porcentagemEsperada) => {
    const usoDoLimite = calcularUsoDoLimite(exposicaoEmReais);
    expect(usoDoLimite.porcentagemMostrada).toBe(porcentagemEsperada);
    expect(usoDoLimite.larguraDaBarraEmPorcentagem).toBe(0);
    expect(usoDoLimite.pertoDoLimite).toBe(false);
  });

  it.each([
    [-95_000_000, '95%', 95, true],
    [-89_999_999.99, '89,99%', 89.99, false],
    [-90_000_000, '90%', 90, true],
    [-78_991, '0,07%', 0.07, false],
  ])('exposição negativa %d usa o valor sem sinal: %s', (exposicaoEmReais, porcentagemEsperada, larguraEsperada, pertoDoLimiteEsperado) => {
    expect(calcularUsoDoLimite(exposicaoEmReais)).toEqual({
      porcentagemMostrada: porcentagemEsperada,
      larguraDaBarraEmPorcentagem: larguraEsperada,
      pertoDoLimite: pertoDoLimiteEsperado,
    });
  });

  it.each([
    [0, false],
    [89_990_000, false],
    [89_999_999.99, false],
    [90_000_000, true],
    [99_997_997.01, true],
  ])('exposição %d fica perto do limite (barrinha âmbar)? %s', (exposicaoEmReais, pertoDoLimiteEsperado) => {
    expect(calcularUsoDoLimite(exposicaoEmReais).pertoDoLimite).toBe(pertoDoLimiteEsperado);
  });

  it('acima do limite mostra o número real e a barrinha fica cheia e âmbar', () => {
    expect(calcularUsoDoLimite(100_500_000)).toEqual({
      porcentagemMostrada: '100,5%',
      larguraDaBarraEmPorcentagem: 100,
      pertoDoLimite: true,
    });
  });
});
