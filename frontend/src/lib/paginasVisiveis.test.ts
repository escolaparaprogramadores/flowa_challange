import { describe, expect, it } from 'vitest';
import { contarPaginasDaLista, listarPaginasVisiveis } from './paginasVisiveis';

describe('listarPaginasVisiveis', () => {
  it.each([
    [1, 1, [1]],
    [3, 1, [1, 2, 3]],
    [3, 2, [1, 2, 3]],
    [3, 3, [1, 2, 3]],
    [7, 1, [1, 2, 3, 4, 5, 6, 7]],
    [7, 4, [1, 2, 3, 4, 5, 6, 7]],
    [7, 7, [1, 2, 3, 4, 5, 6, 7]],
    [8, 1, [1, 2, '…', 8]],
    [8, 2, [1, 2, 3, '…', 8]],
    [8, 3, [1, 2, 3, 4, '…', 8]],
    [8, 4, [1, '…', 3, 4, 5, '…', 8]],
    [8, 7, [1, '…', 6, 7, 8]],
    [8, 8, [1, '…', 7, 8]],
    [40, 1, [1, 2, '…', 40]],
    [40, 5, [1, '…', 4, 5, 6, '…', 40]],
    [40, 20, [1, '…', 19, 20, 21, '…', 40]],
    [40, 39, [1, '…', 38, 39, 40]],
    [40, 40, [1, '…', 39, 40]],
  ])('com %i páginas e a atual na %i mostra %j', (totalDePaginas, paginaAtual, itensEsperados) => {
    expect(listarPaginasVisiveis(paginaAtual, totalDePaginas)).toEqual(itensEsperados);
  });

  it('não mostra nada sem páginas', () => {
    expect(listarPaginasVisiveis(1, 0)).toEqual([]);
  });

  it('nunca passa de 7 itens, para a fileira caber no cartão', () => {
    const maiorFileiraEm1000Paginas = Math.max(
      ...Array.from({ length: 1000 }, (_, posicaoDaPagina) => listarPaginasVisiveis(posicaoDaPagina + 1, 1000).length),
    );
    expect(maiorFileiraEm1000Paginas).toBe(7);
  });

  it('leva a atual para dentro do intervalo quando ela passa da última', () => {
    expect(listarPaginasVisiveis(50, 40)).toEqual([1, '…', 39, 40]);
  });

  it('com 1.000 páginas na última mostra a 1000 como fim', () => {
    expect(listarPaginasVisiveis(1000, 1000)).toEqual([1, '…', 999, 1000]);
  });
});

describe('contarPaginasDaLista', () => {
  it.each([
    [0, 0],
    [1, 1],
    [10, 1],
    [11, 2],
    [23, 3],
    [9_999, 1000],
    [10_000, 1000],
    [10_001, 1000],
    [25_000, 1000],
  ])('com %i ordens dá %i páginas', (totalDeOrdens, paginasEsperadas) => {
    expect(contarPaginasDaLista(totalDeOrdens)).toBe(paginasEsperadas);
  });
});
