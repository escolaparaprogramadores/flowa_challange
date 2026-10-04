import { expect, test, type Locator, type Page } from '@playwright/test';

// A casca da página (F3): 90% de tamanho, conteúdo até 1760 px de CSS com margens de 40 px,
// fundo com brilho e grade, topo com o logo e a linha, e a linha de baixo em duas colunas.
// getBoundingClientRect devolve a medida NA TELA (já com os 90%); px de CSS = tela ÷ 0,9.
const ESCALA_DA_PAGINA = 0.9;
const LARGURA_MAXIMA_DO_CONTEUDO_EM_CSS = 1760;
const MARGEM_LATERAL_EM_CSS = 40;
const LARGURA_DA_NOVA_ORDEM_EM_CSS = 400;
const COR_DA_LINHA_DO_TOPO = 'rgb(23, 42, 45)';
const TEXTO_DE_APOIO =
  'Envie ordens de compra e venda de PETR4, VALE3 e VIIA4 e acompanhe a exposição de cada ativo até o limite de R$ 100.000.000,00.';

async function abrirBoletaNaLargura(paginaDaBoleta: Page, larguraDaJanela: number) {
  await paginaDaBoleta.setViewportSize({ width: larguraDaJanela, height: 1000 });
  await paginaDaBoleta.goto('/');
  await expect(paginaDaBoleta.getByTestId('exposicao-PETR4')).toBeVisible();
}

async function medirNaTela(elementoDaPagina: Locator) {
  return elementoDaPagina.evaluate((elementoNaPagina) => {
    const caixaNaTela = elementoNaPagina.getBoundingClientRect();
    return { esquerda: caixaNaTela.left, direita: caixaNaTela.right, topo: caixaNaTela.top, fim: caixaNaTela.bottom, largura: caixaNaTela.width };
  });
}

test('CA-1: no alto há a barra com o logo à esquerda e a linha fina embaixo; abaixo, o título e o texto de apoio de hoje', async ({ page }) => {
  await abrirBoletaNaLargura(page, 1440);
  const barraDoTopo = page.getByRole('banner');
  await expect(barraDoTopo.getByRole('img', { name: 'Base investimentos' })).toBeVisible();
  // Com os 90%, o Chrome guarda a linha de 1 px da tela como 1,111 px de CSS; o que conta é a tela.
  const espessuraDaLinhaNaTela = await barraDoTopo.evaluate(
    (barraNaPagina) => parseFloat(getComputedStyle(barraNaPagina).borderBottomWidth) * parseFloat(getComputedStyle(document.documentElement).zoom),
  );
  expect(espessuraDaLinhaNaTela).toBeCloseTo(1, 3);
  await expect(barraDoTopo).toHaveCSS('border-bottom-style', 'solid');
  await expect(barraDoTopo).toHaveCSS('border-bottom-color', COR_DA_LINHA_DO_TOPO);
  await expect(page.getByRole('navigation')).toHaveCount(0);

  const caixaDaBarra = await medirNaTela(barraDoTopo);
  const caixaDoLogo = await medirNaTela(barraDoTopo.getByRole('img', { name: 'Base investimentos' }));
  expect(caixaDoLogo.esquerda).toBeCloseTo(caixaDaBarra.esquerda, 0);

  const tituloDaPagina = page.getByRole('heading', { level: 1 });
  await expect(tituloDaPagina).toHaveText('Boleta de ordens');
  await expect(tituloDaPagina).toHaveCSS('font-family', /^Sora/);
  await expect(tituloDaPagina).toHaveCSS('font-size', '34px');
  await expect(tituloDaPagina).toHaveCSS('font-weight', '600');
  await expect(tituloDaPagina).toHaveCSS('letter-spacing', '-1.02px');
  expect((await medirNaTela(tituloDaPagina)).topo).toBeGreaterThan(caixaDaBarra.fim);
  await expect(page.locator('.apoio')).toHaveText(TEXTO_DE_APOIO);
});

test('CA-2: com o navegador em 100%, a página aparece em 90% e um texto de 15 px mede 13,5 px na tela', async ({ page }) => {
  await abrirBoletaNaLargura(page, 1440);
  expect(await page.evaluate(() => window.devicePixelRatio)).toBe(1);
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).zoom)).toBe(String(ESCALA_DA_PAGINA));

  const textoDeApoio = page.locator('.apoio');
  await expect(textoDeApoio).toHaveCSS('font-size', '15px');
  // A boleta tem 400 px de CSS; o quanto ela mede na tela é a escala real aplicada a tudo, letras inclusive.
  const larguraDaBoletaNaTela = (await medirNaTela(page.getByRole('form', { name: 'Boleta de ordem' }))).largura;
  const escalaMedidaNaTela = larguraDaBoletaNaTela / LARGURA_DA_NOVA_ORDEM_EM_CSS;
  expect(escalaMedidaNaTela).toBeCloseTo(ESCALA_DA_PAGINA, 3);
  expect(15 * escalaMedidaNaTela).toBeCloseTo(13.5, 2);
  await page.screenshot({ path: test.info().outputPath('ca-2-pagina-em-90.png') });
});

for (const larguraDaJanela of [1440, 1920]) {
  test(`CA-3: em ${larguraDaJanela} px o conteúdo usa até 1760 px de CSS, com margens de 40 px de CSS`, async ({ page }) => {
    await abrirBoletaNaLargura(page, larguraDaJanela);
    const larguraDaJanelaEmCss = larguraDaJanela / ESCALA_DA_PAGINA;
    const larguraEsperadaDoConteudoEmCss = Math.min(LARGURA_MAXIMA_DO_CONTEUDO_EM_CSS, larguraDaJanelaEmCss - 2 * MARGEM_LATERAL_EM_CSS);
    const caixaDoConteudo = await medirNaTela(page.getByRole('banner'));
    expect(caixaDoConteudo.largura / ESCALA_DA_PAGINA).toBeCloseTo(larguraEsperadaDoConteudoEmCss, 0);
    expect(caixaDoConteudo.esquerda / ESCALA_DA_PAGINA).toBeGreaterThanOrEqual(MARGEM_LATERAL_EM_CSS - 0.5);
    // Centralizado: a faixa vazia da esquerda é igual à da direita.
    expect(caixaDoConteudo.esquerda).toBeCloseTo(larguraDaJanela - caixaDoConteudo.direita, 0);
    if (larguraDaJanela === 1440) expect(caixaDoConteudo.esquerda / ESCALA_DA_PAGINA).toBeCloseTo(MARGEM_LATERAL_EM_CSS, 0);
    // Em 1920 a faixa vazia de cada lado era de 328 px na tela (1360 px de largura máxima, 100%); agora é 168 px.
    if (larguraDaJanela === 1920) expect(caixaDoConteudo.esquerda).toBeCloseTo((1920 - LARGURA_MAXIMA_DO_CONTEUDO_EM_CSS * ESCALA_DA_PAGINA) / 2, 0);
    await page.screenshot({ path: test.info().outputPath(`ca-3-largura-${larguraDaJanela}.png`) });
  });
}

test('CA-4: o fundo tem brilho verde no alto e uma grade fina que some de cima para baixo', async ({ page }) => {
  await abrirBoletaNaLargura(page, 1440);
  const desenhoDoFundo = await page.evaluate(() => {
    const estiloDoFundo = getComputedStyle(document.body, '::before');
    return { imagens: estiloDoFundo.backgroundImage, mascara: estiloDoFundo.maskImage, posicao: estiloDoFundo.position, camada: estiloDoFundo.zIndex, cliques: estiloDoFundo.pointerEvents };
  });
  expect(desenhoDoFundo.imagens).toContain('radial-gradient(60% 520px at 50% -140px, rgba(79, 227, 176, 0.11), rgba(0, 0, 0, 0) 70%)');
  expect(desenhoDoFundo.imagens).toContain('linear-gradient(to right, rgba(232, 239, 238, 0.035) 1px, rgba(0, 0, 0, 0) 1px)');
  expect(desenhoDoFundo.imagens).toContain('linear-gradient(rgba(232, 239, 238, 0.035) 1px, rgba(0, 0, 0, 0) 1px)');
  expect(desenhoDoFundo.mascara).toBe('linear-gradient(rgb(0, 0, 0) 0px, rgba(0, 0, 0, 0) 100%)');
  expect(desenhoDoFundo).toMatchObject({ posicao: 'absolute', camada: '-1', cliques: 'none' });
});

for (const larguraDaJanela of [1440, 1920]) {
  test(`CA-7: em ${larguraDaJanela} px, abaixo dos ativos, a resposta (mais larga) e a Nova ordem (400 px) começam e terminam na mesma altura`, async ({ page }) => {
    await abrirBoletaNaLargura(page, larguraDaJanela);
    const cartaoDaEsquerda = page.locator('section.resposta');
    const caixaDosAtivos = await medirNaTela(page.locator('section.exposicao'));
    const caixaDaEsquerda = await medirNaTela(cartaoDaEsquerda);
    const caixaDaNovaOrdem = await medirNaTela(page.getByRole('form', { name: 'Boleta de ordem' }));

    expect(caixaDaEsquerda.topo).toBeGreaterThan(caixaDosAtivos.fim);
    expect(caixaDaEsquerda.direita).toBeLessThan(caixaDaNovaOrdem.esquerda);
    expect(caixaDaEsquerda.largura).toBeGreaterThan(caixaDaNovaOrdem.largura);
    expect(caixaDaNovaOrdem.largura / ESCALA_DA_PAGINA).toBeCloseTo(LARGURA_DA_NOVA_ORDEM_EM_CSS, 1);
    expect(caixaDaEsquerda.topo - caixaDaNovaOrdem.topo).toBe(0);
    expect(caixaDaEsquerda.fim - caixaDaNovaOrdem.fim).toBe(0);
    await expect(cartaoDaEsquerda).toHaveCSS('border-radius', '22px');
  });
}

test('CA-40: ao abrir a tela nenhuma requisição sai para outro domínio e nenhuma imagem é baixada', async ({ page }) => {
  const enderecosPedidos: string[] = [];
  const imagensPedidas: string[] = [];
  page.on('request', (requisicaoDaPagina) => {
    enderecosPedidos.push(requisicaoDaPagina.url());
    if (requisicaoDaPagina.resourceType() === 'image') imagensPedidas.push(requisicaoDaPagina.url());
  });
  await abrirBoletaNaLargura(page, 1440);
  await page.waitForLoadState('networkidle');
  const origemDaTela = new URL(page.url()).origin;
  expect(enderecosPedidos.length).toBeGreaterThan(0);
  expect(enderecosPedidos.filter((enderecoPedido) => new URL(enderecoPedido).origin !== origemDaTela)).toEqual([]);
  expect(imagensPedidas).toEqual([]);
});
