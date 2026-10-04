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

async function medirCaixaDoElementoNaTela(elementoDaPagina: Locator) {
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
  // Os links do Datadog entram no topo (F4); o título e o texto de apoio não têm link.
  await expect(page.locator('.cabecalho-da-pagina').getByRole('link')).toHaveCount(0);

  const caixaDaBarra = await medirCaixaDoElementoNaTela(barraDoTopo);
  const caixaDoLogo = await medirCaixaDoElementoNaTela(barraDoTopo.getByRole('img', { name: 'Base investimentos' }));
  expect(caixaDoLogo.esquerda).toBeCloseTo(caixaDaBarra.esquerda, 0);

  const tituloDaPagina = page.getByRole('heading', { level: 1 });
  await expect(tituloDaPagina).toHaveText('Boleta de ordens');
  await expect(tituloDaPagina).toHaveCSS('font-family', /^Sora/);
  await expect(tituloDaPagina).toHaveCSS('font-size', '34px');
  await expect(tituloDaPagina).toHaveCSS('font-weight', '600');
  await expect(tituloDaPagina).toHaveCSS('letter-spacing', '-1.02px');
  expect((await medirCaixaDoElementoNaTela(tituloDaPagina)).topo).toBeGreaterThan(caixaDaBarra.fim);
  await expect(page.locator('.apoio')).toHaveText(TEXTO_DE_APOIO);
});

test('CA-2: com o navegador em 100%, a página aparece em 90% e um texto de 15 px mede 13,5 px na tela', async ({ page }) => {
  await abrirBoletaNaLargura(page, 1440);
  expect(await page.evaluate(() => window.devicePixelRatio)).toBe(1);
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).zoom)).toBe(String(ESCALA_DA_PAGINA));

  const textoDeApoio = page.locator('.apoio');
  await expect(textoDeApoio).toHaveCSS('font-size', '15px');
  // Letra na tela = tamanho de CSS × zoom efetivo do próprio parágrafo, que o Chrome informa em currentCSSZoom.
  const letraDoApoioNaTela = await textoDeApoio.evaluate(
    (apoioNaPagina) => parseFloat(getComputedStyle(apoioNaPagina).fontSize) * (apoioNaPagina as HTMLElement & { currentCSSZoom: number }).currentCSSZoom,
  );
  expect(letraDoApoioNaTela).toBeCloseTo(13.5, 3);
  // A linha do parágrafo tem 1,6 × 15 = 24 px de CSS; na tela, cada linha mede 21,6 px.
  const alturaDeUmaLinhaNaTela = await textoDeApoio.evaluate((apoioNaPagina) => {
    const textoInteiro = document.createRange();
    textoInteiro.selectNodeContents(apoioNaPagina);
    const quantidadeDeLinhas = new Set([...textoInteiro.getClientRects()].map((pedacoDaLinha) => Math.round(pedacoDaLinha.top))).size;
    return apoioNaPagina.getBoundingClientRect().height / quantidadeDeLinhas;
  });
  expect(alturaDeUmaLinhaNaTela).toBeCloseTo(24 * ESCALA_DA_PAGINA, 0);
  // A boleta tem 400 px de CSS; na tela, a mesma escala vale para as caixas.
  const larguraDaBoletaNaTela = (await medirCaixaDoElementoNaTela(page.getByRole('form', { name: 'Boleta de ordem' }))).largura;
  expect(larguraDaBoletaNaTela / LARGURA_DA_NOVA_ORDEM_EM_CSS).toBeCloseTo(ESCALA_DA_PAGINA, 3);
  await page.screenshot({ path: test.info().outputPath('ca-2-pagina-em-90.png') });
});

for (const larguraDaJanela of [1440, 1920]) {
  test(`CA-3: em ${larguraDaJanela} px o conteúdo usa até 1760 px de CSS, com margens de 40 px de CSS`, async ({ page }) => {
    await abrirBoletaNaLargura(page, larguraDaJanela);
    const larguraDaJanelaEmCss = larguraDaJanela / ESCALA_DA_PAGINA;
    const larguraEsperadaDoConteudoEmCss = Math.min(LARGURA_MAXIMA_DO_CONTEUDO_EM_CSS, larguraDaJanelaEmCss - 2 * MARGEM_LATERAL_EM_CSS);
    const caixaDoConteudo = await medirCaixaDoElementoNaTela(page.getByRole('banner'));
    expect(caixaDoConteudo.largura / ESCALA_DA_PAGINA).toBeCloseTo(larguraEsperadaDoConteudoEmCss, 0);
    // Topo e cartões ocupam a mesma faixa: a grade não pode ser mais larga nem mais estreita que o topo.
    const caixaDaGrade = await medirCaixaDoElementoNaTela(page.getByRole('main'));
    expect(caixaDaGrade.esquerda).toBeCloseTo(caixaDoConteudo.esquerda, 1);
    expect(caixaDaGrade.largura).toBeCloseTo(caixaDoConteudo.largura, 1);
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
    return {
      conteudo: estiloDoFundo.content,
      altura: estiloDoFundo.height,
      imagens: estiloDoFundo.backgroundImage,
      mascara: estiloDoFundo.maskImage,
      posicao: estiloDoFundo.position,
      camada: estiloDoFundo.zIndex,
      cliques: estiloDoFundo.pointerEvents,
    };
  });
  expect(desenhoDoFundo).toMatchObject({ conteudo: '""', altura: '900px' });
  expect(desenhoDoFundo.imagens).toContain('radial-gradient(60% 520px at 50% -140px, rgba(79, 227, 176, 0.11), rgba(0, 0, 0, 0) 70%)');
  expect(desenhoDoFundo.imagens).toContain('linear-gradient(to right, rgba(232, 239, 238, 0.035) 1px, rgba(0, 0, 0, 0) 1px)');
  expect(desenhoDoFundo.imagens).toContain('linear-gradient(rgba(232, 239, 238, 0.035) 1px, rgba(0, 0, 0, 0) 1px)');
  expect(desenhoDoFundo.mascara).toBe('linear-gradient(rgb(0, 0, 0) 0px, rgba(0, 0, 0, 0) 100%)');
  expect(desenhoDoFundo).toMatchObject({ posicao: 'absolute', camada: '-1', cliques: 'none' });

  // Prova pela tela: no alto, num ponto vazio, o pixel é mais verde que o fundo liso; embaixo, depois
  // que a grade some (900 px de CSS = 810 px na tela), o pixel volta a ser o fundo liso #081113.
  const printDaTela = await page.screenshot();
  const [pixelDoAlto, pixelDeBaixo] = await page.evaluate(async (printEmBase64) => {
    const printNaPagina = new Image();
    printNaPagina.src = 'data:image/png;base64,' + printEmBase64;
    await printNaPagina.decode();
    const telaDeLeitura = document.createElement('canvas');
    telaDeLeitura.width = printNaPagina.width;
    telaDeLeitura.height = printNaPagina.height;
    const pincelDeLeitura = telaDeLeitura.getContext('2d')!;
    pincelDeLeitura.drawImage(printNaPagina, 0, 0);
    const lerCorDoPixel = (posicaoX: number, posicaoY: number) => [...pincelDeLeitura.getImageData(posicaoX, posicaoY, 1, 1).data.slice(0, 3)];
    return [lerCorDoPixel(720, 4), lerCorDoPixel(8, 990)];
  }, printDaTela.toString('base64'));
  // O canvas pode mudar 1 ponto por canal ao converter a cor do print.
  [8, 17, 19].forEach((canalDoFundoLiso, indiceDoCanal) => expect(Math.abs(pixelDeBaixo[indiceDoCanal] - canalDoFundoLiso)).toBeLessThanOrEqual(1));
  expect(pixelDoAlto[1] - pixelDeBaixo[1], `verde no alto ${pixelDoAlto} x embaixo ${pixelDeBaixo}`).toBeGreaterThanOrEqual(8);
});

for (const larguraDaJanela of [861, 1280, 1440, 1920]) {
  test(`CA-7: em ${larguraDaJanela} px, abaixo dos ativos, a resposta (mais larga) e a Nova ordem (400 px) começam e terminam na mesma altura`, async ({ page }) => {
    await abrirBoletaNaLargura(page, larguraDaJanela);
    const cartaoDaEsquerda = page.locator('section.resposta');
    const caixaDosAtivos = await medirCaixaDoElementoNaTela(page.locator('section.exposicao'));
    const caixaDaEsquerda = await medirCaixaDoElementoNaTela(cartaoDaEsquerda);
    const caixaDaNovaOrdem = await medirCaixaDoElementoNaTela(page.getByRole('form', { name: 'Boleta de ordem' }));

    expect(caixaDaEsquerda.topo).toBeGreaterThan(caixaDosAtivos.fim);
    expect(caixaDaEsquerda.direita).toBeLessThan(caixaDaNovaOrdem.esquerda);
    expect(caixaDaEsquerda.largura).toBeGreaterThan(caixaDaNovaOrdem.largura);
    expect(caixaDaNovaOrdem.largura / ESCALA_DA_PAGINA).toBeCloseTo(LARGURA_DA_NOVA_ORDEM_EM_CSS, 1);
    expect(caixaDaEsquerda.topo - caixaDaNovaOrdem.topo).toBe(0);
    expect(caixaDaEsquerda.fim - caixaDaNovaOrdem.fim).toBe(0);
    await expect(cartaoDaEsquerda).toHaveCSS('border-radius', '22px');

    // "Exposição por ativo" ocupa a largura toda; as duas colunas de baixo encostam nas bordas do conteúdo.
    const caixaDoConteudo = await medirCaixaDoElementoNaTela(page.getByRole('main'));
    expect(caixaDosAtivos.esquerda).toBeCloseTo(caixaDoConteudo.esquerda, 1);
    expect(caixaDosAtivos.largura).toBeCloseTo(caixaDoConteudo.largura, 1);
    expect(caixaDaEsquerda.esquerda).toBeCloseTo(caixaDoConteudo.esquerda, 1);
    expect(caixaDaNovaOrdem.direita).toBeCloseTo(caixaDoConteudo.direita, 1);
  });
}

const SIMBOLOS_DOS_ATIVOS = ['PETR4', 'VALE3', 'VIIA4'];

for (const { larguraDaJanela, ladoALado } of [
  { larguraDaJanela: 375, ladoALado: false },
  { larguraDaJanela: 860, ladoALado: false },
  { larguraDaJanela: 861, ladoALado: true },
  { larguraDaJanela: 1440, ladoALado: true },
]) {
  test(`ASSUMI-09: em ${larguraDaJanela} px os três ativos ficam ${ladoALado ? 'lado a lado, do mesmo tamanho' : 'um embaixo do outro, na largura toda'}`, async ({ page }) => {
    await abrirBoletaNaLargura(page, larguraDaJanela);
    const caixaDaLista = await medirCaixaDoElementoNaTela(page.locator('.exposicao-lista'));
    const caixasDosAtivos = [];
    for (const simboloDoAtivo of SIMBOLOS_DOS_ATIVOS) caixasDosAtivos.push(await medirCaixaDoElementoNaTela(page.getByTestId(`exposicao-${simboloDoAtivo}`)));
    const [caixaDoPrimeiro, caixaDoSegundo, caixaDoTerceiro] = caixasDosAtivos;
    if (ladoALado) {
      expect(caixaDoSegundo.topo).toBeCloseTo(caixaDoPrimeiro.topo, 1);
      expect(caixaDoTerceiro.topo).toBeCloseTo(caixaDoPrimeiro.topo, 1);
      expect(caixaDoSegundo.esquerda).toBeCloseTo(caixaDoPrimeiro.direita, 1);
      expect(caixaDoTerceiro.esquerda).toBeCloseTo(caixaDoSegundo.direita, 1);
      expect(caixaDoTerceiro.direita).toBeCloseTo(caixaDaLista.direita, 1);
      for (const caixaDoAtivo of caixasDosAtivos) expect(caixaDoAtivo.largura).toBeCloseTo(caixaDaLista.largura / 3, 0);
    } else {
      expect(caixaDoSegundo.topo).toBeCloseTo(caixaDoPrimeiro.fim, 1);
      expect(caixaDoTerceiro.topo).toBeCloseTo(caixaDoSegundo.fim, 1);
      for (const caixaDoAtivo of caixasDosAtivos) {
        expect(caixaDoAtivo.esquerda).toBeCloseTo(caixaDaLista.esquerda, 1);
        expect(caixaDoAtivo.largura).toBeCloseTo(caixaDaLista.largura, 1);
      }
    }
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
