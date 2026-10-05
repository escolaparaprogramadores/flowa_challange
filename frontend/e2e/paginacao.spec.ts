import { expect, test, type Locator, type Page, type Request } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import path from 'node:path';
import { ROTA_DAS_ORDENS, ROTA_DE_CRIACAO_DE_ORDEM } from '../src/ordensService';

// Fala com o OrderGenerator e o OrderAccumulator de verdade. Cada cenário apaga tudo pela rota
// DELETE /api/orders e cria pela API só as ordens de que precisa. O cenário do teto de 1.000 páginas
// troca a resposta da listagem por uma montada aqui, porque 10.001 ordens reais não cabem num E2E.

const PASTA_DAS_PROVAS = process.env.F7_PASTA_DAS_PROVAS ?? path.resolve('test-results', 'provas-paginacao');
const NOME_DA_PAGINACAO = 'Páginas da lista de ordens';
const COR_DA_PAGINA_ATUAL = { fundo: 'rgb(232, 239, 238)', texto: 'rgb(8, 17, 19)' };
const COR_DO_ANEL_DE_FOCO = 'rgb(79, 227, 176)';
const LARGURA_DO_ANEL_DE_FOCO_EM_PX_DE_CSS = 2;
const CONTRASTE_MINIMO_DE_TEXTO = 4.5;
const ALTURA_DO_BOTAO_DA_PAGINACAO_EM_PX_DE_CSS = 32;

type OrdemGravadaNoServidor = { receivedAt: string; status: string; symbol: string | null; side: string | null; quantity: number; price: number; orderId: string; clOrdId: string };
type PaginaDeOrdensNoServidor = { page: number; pageSize: number; total: number; orders: OrdemGravadaNoServidor[] };

async function apagarTodasAsOrdensNoServidor(paginaDaBoleta: Page) {
  const respostaDoApagamento = await paginaDaBoleta.request.delete(ROTA_DAS_ORDENS);
  expect(respostaDoApagamento.status()).toBe(204);
}

async function criarOrdensPelaApi(paginaDaBoleta: Page, quantidadeDeOrdens: number) {
  for (let posicaoDaOrdem = 0; posicaoDaOrdem < quantidadeDeOrdens; posicaoDaOrdem++) {
    const respostaDaCriacao = await paginaDaBoleta.request.post(ROTA_DE_CRIACAO_DE_ORDEM, {
      data: { symbol: 'PETR4', side: 'buy', quantity: 1, price: 1 },
    });
    expect(respostaDaCriacao.status()).toBe(200);
  }
}

async function lerPaginaNoServidor(paginaDaBoleta: Page, numeroDaPagina: number): Promise<PaginaDeOrdensNoServidor> {
  const respostaDaLista = await paginaDaBoleta.request.get(`${ROTA_DAS_ORDENS}?page=${numeroDaPagina}`);
  expect(respostaDaLista.status()).toBe(200);
  return (await respostaDaLista.json()) as PaginaDeOrdensNoServidor;
}

function eLeituraDaListaDeOrdens(requisicaoHttp: Request) {
  return requisicaoHttp.method() === 'GET' && new URL(requisicaoHttp.url()).pathname === ROTA_DAS_ORDENS;
}

function paginaPedidaNaLeitura(requisicaoHttp: Request) {
  return new URL(requisicaoHttp.url()).searchParams.get('page');
}

function esperarLeituraDaPagina(paginaDaBoleta: Page, numeroDaPagina: number) {
  return paginaDaBoleta.waitForResponse(
    (respostaHttp) => eLeituraDaListaDeOrdens(respostaHttp.request()) && paginaPedidaNaLeitura(respostaHttp.request()) === String(numeroDaPagina),
  );
}

async function abrirTelaEEsperarPrimeiraPagina(paginaDaBoleta: Page) {
  const leituraDaPrimeiraPagina = esperarLeituraDaPagina(paginaDaBoleta, 1);
  await paginaDaBoleta.goto('/');
  await leituraDaPrimeiraPagina;
}

function cartaoCompraVenda(paginaDaBoleta: Page) {
  return paginaDaBoleta.getByRole('region', { name: 'Compra/Venda' });
}

function paginacaoDaLista(paginaDaBoleta: Page) {
  return cartaoCompraVenda(paginaDaBoleta).getByRole('group', { name: NOME_DA_PAGINACAO });
}

function botaoDaPagina(paginaDaBoleta: Page, numeroDaPagina: number) {
  return paginacaoDaLista(paginaDaBoleta).getByRole('button', { name: `Página ${numeroDaPagina}`, exact: true });
}

function botaoPaginaAnterior(paginaDaBoleta: Page) {
  return paginacaoDaLista(paginaDaBoleta).getByRole('button', { name: 'Página anterior' });
}

function botaoProximaPagina(paginaDaBoleta: Page) {
  return paginacaoDaLista(paginaDaBoleta).getByRole('button', { name: 'Próxima página' });
}

function linhasDaLista(paginaDaBoleta: Page) {
  return cartaoCompraVenda(paginaDaBoleta).getByTestId('linha-da-ordem');
}

async function irParaPaginaPeloBotao(paginaDaBoleta: Page, botaoDaPaginacao: Locator, numeroDaPaginaEsperada: number) {
  const leituraDaPagina = esperarLeituraDaPagina(paginaDaBoleta, numeroDaPaginaEsperada);
  await botaoDaPaginacao.click();
  await leituraDaPagina;
}

async function conferirFileiraDaPaginacao(paginaDaBoleta: Page, fileiraEsperada: string[]) {
  await expect(paginacaoDaLista(paginaDaBoleta).locator('ul > li')).toHaveText(fileiraEsperada);
}

async function conferirPaginaAtual(paginaDaBoleta: Page, numeroDaPagina: number) {
  const botaoDaPaginaAtual = botaoDaPagina(paginaDaBoleta, numeroDaPagina);
  await expect(botaoDaPaginaAtual).toHaveAttribute('aria-current', 'page');
  await expect(paginacaoDaLista(paginaDaBoleta).locator('[aria-current="page"]')).toHaveCount(1);
  await expect(botaoDaPaginaAtual).toHaveCSS('background-color', COR_DA_PAGINA_ATUAL.fundo);
  await expect(botaoDaPaginaAtual).toHaveCSS('color', COR_DA_PAGINA_ATUAL.texto);
}

async function conferirLinhasIguaisAoServidor(paginaDaBoleta: Page, numeroDaPagina: number) {
  const paginaNoServidor = await lerPaginaNoServidor(paginaDaBoleta, numeroDaPagina);
  await expect(linhasDaLista(paginaDaBoleta)).toHaveCount(paginaNoServidor.orders.length);
  await expect(linhasDaLista(paginaDaBoleta).locator('td[data-coluna="identificador-do-envio"]')).toHaveText(
    paginaNoServidor.orders.map((ordemNoServidor) => ordemNoServidor.clOrdId),
  );
}

async function salvarProva(paginaDaBoleta: Page, nomeDoArquivo: string) {
  mkdirSync(PASTA_DAS_PROVAS, { recursive: true });
  await paginaDaBoleta.screenshot({ path: path.join(PASTA_DAS_PROVAS, nomeDoArquivo), fullPage: true });
}

function luminanciaRelativa(corRgb: string) {
  const [vermelho, verde, azul] = (corRgb.match(/\d+(\.\d+)?/g) ?? []).slice(0, 3).map((canalDeCor) => {
    const canalDe0a1 = Number(canalDeCor) / 255;
    return canalDe0a1 <= 0.03928 ? canalDe0a1 / 12.92 : ((canalDe0a1 + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * vermelho + 0.7152 * verde + 0.0722 * azul;
}

function contrasteEntreCores(corDoTexto: string, corDoFundo: string) {
  const luminanciaDoTexto = luminanciaRelativa(corDoTexto);
  const luminanciaDoFundo = luminanciaRelativa(corDoFundo);
  return (Math.max(luminanciaDoTexto, luminanciaDoFundo) + 0.05) / (Math.min(luminanciaDoTexto, luminanciaDoFundo) + 0.05);
}

// Botão desligado tem fundo transparente: o fundo que o olho vê é o do primeiro ancestral com cor.
async function lerCoresVisiveisDoBotao(botaoDaPaginacao: Locator) {
  return botaoDaPaginacao.evaluate((elementoDoBotao) => {
    let elementoComFundo: Element | null = elementoDoBotao;
    let corDoFundoVisivel = 'rgba(0, 0, 0, 0)';
    while (elementoComFundo) {
      const corDoFundoDoElemento = getComputedStyle(elementoComFundo).backgroundColor;
      if (corDoFundoDoElemento !== 'rgba(0, 0, 0, 0)' && corDoFundoDoElemento !== 'transparent') {
        corDoFundoVisivel = corDoFundoDoElemento;
        break;
      }
      elementoComFundo = elementoComFundo.parentElement;
    }
    return { corDoTexto: getComputedStyle(elementoDoBotao).color, corDoFundo: corDoFundoVisivel };
  });
}

async function lerEscalaDaPagina(paginaDaBoleta: Page) {
  return paginaDaBoleta.evaluate(() => Number(getComputedStyle(document.documentElement).getPropertyValue('--escala-da-pagina')));
}

test.beforeEach(async ({ page }) => {
  await apagarTodasAsOrdensNoServidor(page);
});

// As specs seguintes contam com o banco sem as dezenas de ordens criadas aqui.
test.afterEach(async ({ page }) => {
  await apagarTodasAsOrdensNoServidor(page);
});

test('ASSUMI-01: com 10 ordens a lista mostra as 10 e não aparece paginação', async ({ page }) => {
  await criarOrdensPelaApi(page, 10);
  await abrirTelaEEsperarPrimeiraPagina(page);
  await expect(linhasDaLista(page)).toHaveCount(10);
  await expect(paginacaoDaLista(page)).toHaveCount(0);
});

test('CA-13: com 23 ordens a paginação vai à página 2, à 3 e volta à 1 mostrando as ordens de cada uma', async ({ page }) => {
  await criarOrdensPelaApi(page, 23);
  await abrirTelaEEsperarPrimeiraPagina(page);

  await expect(paginacaoDaLista(page)).toBeVisible();
  // A paginação é um grupo do cartão, não um menu: a regra "sem menu" da página continua valendo.
  await expect(page.getByRole('navigation')).toHaveCount(0);
  await expect(page.getByTestId('resumo-da-paginacao')).toHaveText('Mostrando 1–10 de 23 ordens');
  await conferirFileiraDaPaginacao(page, ['‹', '1', '2', '3', '›']);
  await conferirPaginaAtual(page, 1);
  await expect(botaoPaginaAnterior(page)).toBeDisabled();
  await expect(botaoProximaPagina(page)).toBeEnabled();
  await conferirLinhasIguaisAoServidor(page, 1);
  await salvarProva(page, '07-paginacao-1440.png');

  await irParaPaginaPeloBotao(page, botaoDaPagina(page, 2), 2);
  await conferirPaginaAtual(page, 2);
  await expect(page.getByTestId('resumo-da-paginacao')).toHaveText('Mostrando 11–20 de 23 ordens');
  await expect(botaoPaginaAnterior(page)).toBeEnabled();
  await expect(botaoProximaPagina(page)).toBeEnabled();
  await conferirLinhasIguaisAoServidor(page, 2);

  await irParaPaginaPeloBotao(page, botaoProximaPagina(page), 3);
  await conferirPaginaAtual(page, 3);
  await expect(page.getByTestId('resumo-da-paginacao')).toHaveText('Mostrando 21–23 de 23 ordens');
  await expect(botaoProximaPagina(page)).toBeDisabled();
  await conferirLinhasIguaisAoServidor(page, 3);

  await irParaPaginaPeloBotao(page, botaoPaginaAnterior(page), 2);
  await conferirPaginaAtual(page, 2);
  await irParaPaginaPeloBotao(page, botaoDaPagina(page, 1), 1);
  await conferirPaginaAtual(page, 1);
  await expect(page.getByTestId('resumo-da-paginacao')).toHaveText('Mostrando 1–10 de 23 ordens');
  await expect(botaoPaginaAnterior(page)).toBeDisabled();
  await conferirLinhasIguaisAoServidor(page, 1);

  const escalaDaPagina = await lerEscalaDaPagina(page);
  expect(escalaDaPagina).toBe(0.9);
  const caixaDoBotaoDaPagina = await botaoDaPagina(page, 2).boundingBox();
  expect((caixaDoBotaoDaPagina?.height ?? 0) / escalaDaPagina).toBeCloseTo(ALTURA_DO_BOTAO_DA_PAGINACAO_EM_PX_DE_CSS, 0);
});

test('CA-37: com 8 páginas a fileira encurta com "…" não clicável e mostra a primeira, a última, a atual e as vizinhas', async ({ page }) => {
  await criarOrdensPelaApi(page, 75);
  await abrirTelaEEsperarPrimeiraPagina(page);
  await expect(page.getByTestId('resumo-da-paginacao')).toHaveText('Mostrando 1–10 de 75 ordens');
  await conferirFileiraDaPaginacao(page, ['‹', '1', '2', '…', '8', '›']);

  const reticenciasDaPaginacao = paginacaoDaLista(page).getByTestId('reticencias-da-paginacao');
  await expect(reticenciasDaPaginacao).toHaveCount(1);
  const naturezaDasReticencias = await reticenciasDaPaginacao.evaluate((elementoDasReticencias) => ({
    etiqueta: elementoDasReticencias.tagName,
    controlesDentro: elementoDasReticencias.querySelectorAll('button, a, [tabindex]').length,
    ordemNoTab: (elementoDasReticencias as HTMLElement).tabIndex,
  }));
  expect(naturezaDasReticencias).toEqual({ etiqueta: 'LI', controlesDentro: 0, ordemNoTab: -1 });

  // Clicar no "…" não pode pedir página nenhuma: depois dele, a única leitura é a da página 2.
  const leiturasDepoisDoCliqueNasReticencias: string[] = [];
  page.on('request', (requisicaoHttp) => {
    if (eLeituraDaListaDeOrdens(requisicaoHttp)) leiturasDepoisDoCliqueNasReticencias.push(paginaPedidaNaLeitura(requisicaoHttp) ?? 'sem-pagina');
  });
  await reticenciasDaPaginacao.click();
  await irParaPaginaPeloBotao(page, botaoDaPagina(page, 2), 2);
  expect(leiturasDepoisDoCliqueNasReticencias).toEqual(['2']);
  await conferirFileiraDaPaginacao(page, ['‹', '1', '2', '3', '…', '8', '›']);

  await irParaPaginaPeloBotao(page, botaoDaPagina(page, 3), 3);
  await conferirFileiraDaPaginacao(page, ['‹', '1', '2', '3', '4', '…', '8', '›']);
  await irParaPaginaPeloBotao(page, botaoDaPagina(page, 4), 4);
  await conferirFileiraDaPaginacao(page, ['‹', '1', '…', '3', '4', '5', '…', '8', '›']);
  await conferirPaginaAtual(page, 4);
  await expect(page.getByTestId('resumo-da-paginacao')).toHaveText('Mostrando 31–40 de 75 ordens');
  await conferirLinhasIguaisAoServidor(page, 4);

  const caixaDoCartao = await cartaoCompraVenda(page).boundingBox();
  const caixaDaPaginacao = await paginacaoDaLista(page).boundingBox();
  expect(caixaDaPaginacao!.x).toBeGreaterThanOrEqual(caixaDoCartao!.x);
  expect(caixaDaPaginacao!.x + caixaDaPaginacao!.width).toBeLessThanOrEqual(caixaDoCartao!.x + caixaDoCartao!.width);
  await salvarProva(page, '07-paginacao-reticencias-1440.png');

  await irParaPaginaPeloBotao(page, botaoDaPagina(page, 8), 8);
  await conferirFileiraDaPaginacao(page, ['‹', '1', '…', '7', '8', '›']);
  await expect(page.getByTestId('resumo-da-paginacao')).toHaveText('Mostrando 71–75 de 75 ordens');
  await expect(botaoProximaPagina(page)).toBeDisabled();
  await conferirLinhasIguaisAoServidor(page, 8);
});

test('CA-38: enviar uma ordem estando na página 2 volta a lista para a página 1 com a ordem nova no topo, sem recarregar', async ({ page }) => {
  await criarOrdensPelaApi(page, 15);
  await abrirTelaEEsperarPrimeiraPagina(page);
  await irParaPaginaPeloBotao(page, botaoDaPagina(page, 2), 2);
  await conferirPaginaAtual(page, 2);
  await page.evaluate(() => {
    (window as unknown as { marcaDaMesmaCarga: string }).marcaDaMesmaCarga = 'sem-recarregar';
  });

  await page.getByRole('group', { name: 'Lado da ordem' }).getByRole('button', { name: 'Venda' }).click();
  await page.getByLabel(/^Quantidade de/).fill('7');
  await page.getByLabel('Preço por ação (R$)').fill('12,34');
  const respostaDaCriacao = page.waitForResponse((respostaHttp) => respostaHttp.request().method() === 'POST' && new URL(respostaHttp.url()).pathname === ROTA_DE_CRIACAO_DE_ORDEM);
  const leituraDaPrimeiraPagina = esperarLeituraDaPagina(page, 1);
  await page.getByRole('button', { name: /^Enviar ordem/ }).click();
  const corpoDaCriacao = (await (await respostaDaCriacao).json()) as { clOrdId: string };
  await leituraDaPrimeiraPagina;

  await conferirPaginaAtual(page, 1);
  await expect(page.getByTestId('resumo-da-paginacao')).toHaveText('Mostrando 1–10 de 16 ordens');
  const linhaDoTopo = linhasDaLista(page).nth(0);
  await expect(linhaDoTopo.locator('td[data-coluna="identificador-do-envio"]')).toHaveText(corpoDaCriacao.clOrdId);
  await expect(linhaDoTopo.locator('td[data-coluna="lado"]')).toHaveText('Venda');
  await expect(linhaDoTopo.locator('td[data-coluna="quantidade"]')).toHaveText('7');
  await expect(linhaDoTopo.locator('td[data-coluna="preco"]')).toHaveText(new Intl.NumberFormat('pt-BR', { style: 'currency', currency: 'BRL' }).format(12.34));
  expect(await page.evaluate(() => (window as unknown as { marcaDaMesmaCarga?: string }).marcaDaMesmaCarga)).toBe('sem-recarregar');
});

test('CA-41: a tela pede ao servidor só a página que mostra, sempre com o número da página', async ({ page }) => {
  await criarOrdensPelaApi(page, 25);
  const paginasPedidasAoServidor: string[] = [];
  page.on('request', (requisicaoHttp) => {
    if (eLeituraDaListaDeOrdens(requisicaoHttp)) paginasPedidasAoServidor.push(paginaPedidaNaLeitura(requisicaoHttp) ?? 'sem-pagina');
  });

  const respostaDaPrimeiraPagina = esperarLeituraDaPagina(page, 1);
  await page.goto('/');
  const corpoDaPrimeiraPagina = (await (await respostaDaPrimeiraPagina).json()) as PaginaDeOrdensNoServidor;
  await expect(page.getByTestId('resumo-da-paginacao')).toHaveText('Mostrando 1–10 de 25 ordens');
  expect(corpoDaPrimeiraPagina.orders).toHaveLength(10);
  expect(corpoDaPrimeiraPagina.total).toBe(25);
  expect(paginasPedidasAoServidor).toEqual(['1']);

  await irParaPaginaPeloBotao(page, botaoDaPagina(page, 3), 3);
  await expect(page.getByTestId('resumo-da-paginacao')).toHaveText('Mostrando 21–25 de 25 ordens');
  await expect(linhasDaLista(page)).toHaveCount(5);
  expect(paginasPedidasAoServidor).toEqual(['1', '3']);
});

test('CA-42: com mais de 10.000 ordens a paginação para na página 1000 e mostra o total real', async ({ page }) => {
  const TOTAL_ACIMA_DO_TETO = 25_000;
  await page.route(
    (urlPedida) => urlPedida.pathname === ROTA_DAS_ORDENS,
    async (rotaInterceptada) => {
      if (rotaInterceptada.request().method() !== 'GET') return rotaInterceptada.fallback();
      const paginaPedida = Number(new URL(rotaInterceptada.request().url()).searchParams.get('page'));
      const ordensMontadas: OrdemGravadaNoServidor[] = Array.from({ length: 10 }, (_, posicaoNaPagina) => {
        const numeroDaOrdem = String((paginaPedida - 1) * 10 + posicaoNaPagina + 1).padStart(32, '0');
        return { receivedAt: '2026-10-04T15:00:00Z', status: 'accepted', symbol: 'PETR4', side: 'buy', quantity: 1, price: 1, orderId: numeroDaOrdem, clOrdId: numeroDaOrdem };
      });
      await rotaInterceptada.fulfill({ json: { page: paginaPedida, pageSize: 10, total: TOTAL_ACIMA_DO_TETO, orders: ordensMontadas } });
    },
  );

  await abrirTelaEEsperarPrimeiraPagina(page);
  await expect(page.getByTestId('resumo-da-paginacao')).toHaveText('Mostrando 1–10 de 25.000 ordens');
  await conferirFileiraDaPaginacao(page, ['‹', '1', '2', '…', '1000', '›']);
  await expect(botaoDaPagina(page, 1001)).toHaveCount(0);

  await irParaPaginaPeloBotao(page, botaoDaPagina(page, 1000), 1000);
  await conferirPaginaAtual(page, 1000);
  await expect(page.getByTestId('resumo-da-paginacao')).toHaveText('Mostrando 9.991–10.000 de 25.000 ordens');
  await conferirFileiraDaPaginacao(page, ['‹', '1', '…', '999', '1000', '›']);
  await expect(botaoProximaPagina(page)).toBeDisabled();
});

test('CA-27: os botões da paginação recebem foco visível pelo Tab e têm contraste legível', async ({ page }) => {
  await criarOrdensPelaApi(page, 23);
  await abrirTelaEEsperarPrimeiraPagina(page);

  const botaoDaPagina2 = botaoDaPagina(page, 2);
  let chegouNoBotaoPeloTab = false;
  for (let toqueNoTab = 0; toqueNoTab < 80 && !chegouNoBotaoPeloTab; toqueNoTab++) {
    await page.keyboard.press('Tab');
    chegouNoBotaoPeloTab = await botaoDaPagina2.evaluate((elementoDoBotao) => elementoDoBotao === document.activeElement);
  }
  expect(chegouNoBotaoPeloTab).toBe(true);
  await expect(botaoDaPagina2).toHaveCSS('outline-style', 'solid');
  await expect(botaoDaPagina2).toHaveCSS('outline-color', COR_DO_ANEL_DE_FOCO);
  // O anel é o de 2px do tema; com a página em 90% o Chromium o arredonda para pixel inteiro da tela.
  const escalaDaPagina = await lerEscalaDaPagina(page);
  const larguraDoAnelDeFoco = await botaoDaPagina2.evaluate((elementoDoBotao) => parseFloat(getComputedStyle(elementoDoBotao).outlineWidth));
  expect(larguraDoAnelDeFoco).toBeCloseTo(Math.floor(LARGURA_DO_ANEL_DE_FOCO_EM_PX_DE_CSS * escalaDaPagina) / escalaDaPagina, 3);
  await salvarProva(page, '07-paginacao-foco-tab-1440.png');

  for (const botaoConferido of [botaoDaPagina2, botaoDaPagina(page, 1), botaoPaginaAnterior(page), botaoProximaPagina(page)]) {
    const coresVisiveisDoBotao = await lerCoresVisiveisDoBotao(botaoConferido);
    expect(contrasteEntreCores(coresVisiveisDoBotao.corDoTexto, coresVisiveisDoBotao.corDoFundo)).toBeGreaterThanOrEqual(CONTRASTE_MINIMO_DE_TEXTO);
  }
});

for (const larguraEstreita of [375, 860]) {
  test(`CA-26: em ${larguraEstreita}px a paginação mais longa cabe no cartão e a página não rola para o lado`, async ({ page }) => {
    await page.setViewportSize({ width: larguraEstreita, height: 900 });
    await criarOrdensPelaApi(page, 75);
    await abrirTelaEEsperarPrimeiraPagina(page);
    await irParaPaginaPeloBotao(page, botaoDaPagina(page, 2), 2);
    await irParaPaginaPeloBotao(page, botaoDaPagina(page, 3), 3);
    await irParaPaginaPeloBotao(page, botaoDaPagina(page, 4), 4);
    await conferirFileiraDaPaginacao(page, ['‹', '1', '…', '3', '4', '5', '…', '8', '›']);

    const larguraDaPagina = await page.evaluate(() => ({ larguraDoConteudo: document.documentElement.scrollWidth, larguraVisivel: document.documentElement.clientWidth }));
    expect(larguraDaPagina.larguraDoConteudo).toBe(larguraDaPagina.larguraVisivel);

    const caixaDoCartao = await cartaoCompraVenda(page).boundingBox();
    const fileiraDeBotoes = paginacaoDaLista(page).locator('ul');
    const caixaDaFileira = await fileiraDeBotoes.boundingBox();
    expect(caixaDaFileira!.x).toBeGreaterThanOrEqual(caixaDoCartao!.x);
    expect(caixaDaFileira!.x + caixaDaFileira!.width).toBeLessThanOrEqual(caixaDoCartao!.x + caixaDoCartao!.width);
    const fileiraSemCorte = await fileiraDeBotoes.evaluate((elementoDaFileira) => elementoDaFileira.scrollWidth <= elementoDaFileira.clientWidth);
    expect(fileiraSemCorte).toBe(true);
    await paginacaoDaLista(page).scrollIntoViewIfNeeded();
    for (const botaoDaFileira of await paginacaoDaLista(page).getByRole('button').all()) {
      await expect(botaoDaFileira).toBeInViewport();
    }
    await salvarProva(page, `07-paginacao-${larguraEstreita}.png`);
  });
}
