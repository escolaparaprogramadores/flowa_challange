import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { CompraVenda, type EstadoDaListaDeOrdens } from './CompraVenda';
import type { OrdemDaLista } from './ordensService';

// Os encaixes são o contrato com a F7 (paginação no rodapé) e a F8 (Deletar tudo no cabeçalho).
const ACAO_DO_CABECALHO = createElement('button', { type: 'button', 'data-testid': 'acao-do-cabecalho-de-teste' }, 'Deletar tudo');
const RODAPE = createElement('nav', { 'data-testid': 'rodape-de-teste' }, 'Página 1');

const ordemAceita: OrdemDaLista = {
  recebidaEm: '2026-10-04T23:30:00Z', situacao: 'aceita', simbolo: 'PETR4', lado: 'Compra',
  quantidade: 1_000, precoEmReais: 10, orderId: 'a'.repeat(32), clOrdId: 'b'.repeat(32),
};

const ESTADOS_DA_LISTA: Array<[string, EstadoDaListaDeOrdens]> = [
  ['carregando', { situacao: 'carregando' }],
  ['erro', { situacao: 'erro', mensagemDeErro: 'Não foi possível ler as ordens agora. Tente de novo em instantes.' }],
  ['vazia', { situacao: 'pronto', paginaDeOrdens: { pagina: 1, totalDeOrdens: 0, ordens: [] } }],
  ['com ordens', { situacao: 'pronto', paginaDeOrdens: { pagina: 1, totalDeOrdens: 1, ordens: [ordemAceita] } }],
];

function desenharCartaoCompraVenda(estadoDaListaDeOrdens: EstadoDaListaDeOrdens) {
  return renderToStaticMarkup(
    createElement(CompraVenda, { estadoDaListaDeOrdens, enviandoOrdem: false, acaoDoCabecalho: ACAO_DO_CABECALHO, rodape: RODAPE }),
  );
}

describe('CompraVenda: encaixes do contrato', () => {
  for (const [nomeDoEstado, estadoDaListaDeOrdens] of ESTADOS_DA_LISTA) {
    it(`RF-15: com a lista ${nomeDoEstado}, a ação do cabeçalho aparece dentro do cabeçalho do cartão, uma vez`, () => {
      const htmlDoCartao = desenharCartaoCompraVenda(estadoDaListaDeOrdens);
      const htmlDoCabecalho = htmlDoCartao.match(/<div class="compra-venda-cabeca">([\s\S]*?)<\/div>/)?.[1] ?? '';
      expect(htmlDoCabecalho).toContain('data-testid="acao-do-cabecalho-de-teste"');
      expect(htmlDoCartao.split('data-testid="acao-do-cabecalho-de-teste"')).toHaveLength(2);
    });
  }

  it('RF-15: com ordens, o rodapé aparece uma vez, depois da tabela', () => {
    const htmlDoCartao = desenharCartaoCompraVenda(ESTADOS_DA_LISTA[3][1]);
    expect(htmlDoCartao.split('data-testid="rodape-de-teste"')).toHaveLength(2);
    expect(htmlDoCartao.indexOf('data-testid="rodape-de-teste"')).toBeGreaterThan(htmlDoCartao.indexOf('</table>'));
  });

  for (const [nomeDoEstado, estadoDaListaDeOrdens] of ESTADOS_DA_LISTA.slice(0, 3)) {
    it(`ASSUMI-05: com a lista ${nomeDoEstado}, o rodapé não é desenhado (CA-14: sem paginação)`, () => {
      expect(desenharCartaoCompraVenda(estadoDaListaDeOrdens)).not.toContain('data-testid="rodape-de-teste"');
    });
  }
});
