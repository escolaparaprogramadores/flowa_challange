import { expect, test, type Locator, type Page } from '@playwright/test';

// Quebras do tema Base (spec RNF-03 e ASSUMI-05): uma coluna até 860 px, duas de 861 a 1179 px
// e três a partir de 1180 px. Cada fronteira é medida dos dois lados.
const LARGURAS_DO_TEMA = [
  { largura: 390, colunasEsperadas: 1 },
  { largura: 860, colunasEsperadas: 1 },
  { largura: 861, colunasEsperadas: 2 },
  { largura: 1179, colunasEsperadas: 2 },
  { largura: 1180, colunasEsperadas: 3 },
  { largura: 1280, colunasEsperadas: 3 },
];
const LARGURAS_DO_CA_23 = [390, 860, 1280];
const COR_DO_ACENTO = 'rgb(79, 227, 176)';
const COR_DO_TEXTO_APAGADO = 'rgb(157, 176, 174)';

// Razão de contraste da WCAG 2 entre duas cores "rgb(r, g, b)" (luminância relativa).
function calcularContrasteWcag(corDoTexto: string, corDoFundo: string) {
  const luminanciaDaCor = (corRgb: string) => {
    const [canalVermelho, canalVerde, canalAzul] = (corRgb.match(/\d+(\.\d+)?/g) ?? []).slice(0, 3).map(Number).map((canalDe0a255) => {
      const canalDe0a1 = canalDe0a255 / 255;
      return canalDe0a1 <= 0.03928 ? canalDe0a1 / 12.92 : ((canalDe0a1 + 0.055) / 1.055) ** 2.4;
    });
    return 0.2126 * canalVermelho + 0.7152 * canalVerde + 0.0722 * canalAzul;
  };
  const [luminanciaMaior, luminanciaMenor] = [luminanciaDaCor(corDoTexto), luminanciaDaCor(corDoFundo)].sort((luminanciaDaPrimeira, luminanciaDaSegunda) => luminanciaDaSegunda - luminanciaDaPrimeira);
  return (luminanciaMaior + 0.05) / (luminanciaMenor + 0.05);
}

async function contarColunasDaGrade(paginaDaBoleta: Page) {
  return paginaDaBoleta.locator('.grade').evaluate((gradeDaPagina) => getComputedStyle(gradeDaPagina).gridTemplateColumns.split(' ').length);
}

function controlesDaBoleta(paginaDaBoleta: Page): Array<[string, Locator]> {
  return [
    ['Compra', paginaDaBoleta.getByRole('button', { name: 'Compra', exact: true })],
    ['Venda', paginaDaBoleta.getByRole('button', { name: 'Venda', exact: true })],
    ['Símbolo', paginaDaBoleta.getByLabel('Símbolo')],
    ['Diminuir quantidade', paginaDaBoleta.getByRole('button', { name: 'Diminuir quantidade' })],
    ['Quantidade', paginaDaBoleta.getByLabel(/^Quantidade de/)],
    ['Aumentar quantidade', paginaDaBoleta.getByRole('button', { name: 'Aumentar quantidade' })],
    ['Preço', paginaDaBoleta.getByLabel('Preço por ação (R$)')],
    ['Enviar', paginaDaBoleta.getByRole('button', { name: /^Enviar ordem/ })],
  ];
}

for (const { largura: larguraDaJanela, colunasEsperadas } of LARGURAS_DO_TEMA) {
  test(`RNF-03: em ${larguraDaJanela} px a página não rola para o lado e a grade tem ${colunasEsperadas} coluna(s)`, async ({ page }) => {
    await page.setViewportSize({ width: larguraDaJanela, height: 900 });
    await page.goto('/');
    await expect(page.getByRole('heading', { name: 'Boleta de ordens' })).toBeVisible();
    const larguraDoConteudo = await page.evaluate(() => document.documentElement.scrollWidth);
    expect(larguraDoConteudo).toBe(larguraDaJanela);
    expect(await contarColunasDaGrade(page)).toBe(colunasEsperadas);
  });
}

test('RF-01/RF-02: página única sem login, com logo, boleta, resposta e exposição, sem menu nem lista de ordens', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByRole('img', { name: 'Base investimentos' })).toBeVisible();
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Boleta de ordens');
  await expect(page.getByRole('heading', { level: 2 })).toHaveText(['Exposição por ativo', 'Resposta da ordem', 'Nova ordem']);
  await expect(page.getByRole('form', { name: 'Boleta de ordem' })).toBeVisible();
  await expect(page.locator('input[type="password"]')).toHaveCount(0);
  await expect(page.getByRole('navigation')).toHaveCount(0);
  await expect(page.getByRole('table')).toHaveCount(0);
  await expect(page.getByRole('link')).toHaveCount(0);
});

test('ASSUMI-05: no celular a boleta vem primeiro, depois a resposta e por último a exposição', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 900 });
  await page.goto('/');
  const topoDaBoleta = (await page.getByRole('form', { name: 'Boleta de ordem' }).boundingBox())!.y;
  const topoDaResposta = (await page.getByRole('heading', { name: 'Resposta da ordem' }).boundingBox())!.y;
  const topoDaExposicao = (await page.getByRole('heading', { name: 'Exposição por ativo' }).boundingBox())!.y;
  expect(topoDaBoleta).toBeLessThan(topoDaResposta);
  expect(topoDaResposta).toBeLessThan(topoDaExposicao);
});

for (const larguraDaJanela of LARGURAS_DO_CA_23) {
  test(`RNF-05: em ${larguraDaJanela} px cada controle da boleta tem área de toque de pelo menos 44 px`, async ({ page }) => {
    await page.setViewportSize({ width: larguraDaJanela, height: 900 });
    await page.goto('/');
    for (const [nomeDoControle, controleDaBoleta] of controlesDaBoleta(page)) {
      await expect(controleDaBoleta, nomeDoControle).toHaveCount(1);
      const caixaDoControle = (await controleDaBoleta.boundingBox())!;
      expect(caixaDoControle.height, `${nomeDoControle}: altura`).toBeGreaterThanOrEqual(44);
      expect(caixaDoControle.width, `${nomeDoControle}: largura`).toBeGreaterThanOrEqual(44);
    }
  });
}

test('RNF-05: o foco pelo teclado é visível em cada controle da boleta, na cor do acento', async ({ page }) => {
  await page.goto('/');
  for (const [nomeDoControle, controleDaBoleta] of controlesDaBoleta(page)) {
    await controleDaBoleta.focus();
    // A borda muda com transição de 180 ms; espera a cor assentar em vez de ler no meio do caminho.
    await expect
      .poll(
        () =>
          controleDaBoleta.evaluate((controleNaPagina) => {
            const estiloDoControle = getComputedStyle(controleNaPagina);
            const molduraDaQuantidade = controleNaPagina.closest('.quantidade');
            const corDoContornoDeFoco = estiloDoControle.outlineStyle === 'solid' ? estiloDoControle.outlineColor : '';
            const corDaBordaDeFoco = (molduraDaQuantidade ? getComputedStyle(molduraDaQuantidade) : estiloDoControle).borderTopColor;
            return [corDoContornoDeFoco, corDaBordaDeFoco];
          }),
        { message: nomeDoControle },
      )
      .toContain(COR_DO_ACENTO);
  }
});

test('RNF-04: o texto de exemplo do preço usa a cor apagada do tema, com contraste de pelo menos 4,5:1', async ({ page }) => {
  await page.goto('/');
  const campoDoPreco = page.getByLabel('Preço por ação (R$)');
  const coresDoExemplo = await campoDoPreco.evaluate((campoNaPagina) => ({
    corDoTexto: getComputedStyle(campoNaPagina, '::placeholder').color,
    corDoFundo: getComputedStyle(campoNaPagina).backgroundColor,
  }));
  expect(coresDoExemplo.corDoTexto).toBe(COR_DO_TEXTO_APAGADO);
  expect(calcularContrasteWcag(coresDoExemplo.corDoTexto, coresDoExemplo.corDoFundo)).toBeGreaterThanOrEqual(4.5);
});

test('RNF-01: a página declara o esquema escuro, para seleção, rolagem e controles nativos seguirem o tema', async ({ page }) => {
  await page.goto('/');
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe('dark');
});

for (const larguraDaJanela of [390, 1024, 1179, 1280]) {
  test(`RNF-03: em ${larguraDaJanela} px os rótulos do painel de exposição cabem numa linha`, async ({ page }) => {
    await page.setViewportSize({ width: larguraDaJanela, height: 900 });
    await page.goto('/');
    for (const simboloDaExposicao of ['PETR4', 'VALE3', 'VIIA4']) {
      for (const rotuloDaExposicao of ['Exposição atual', 'Falta até o limite']) {
        const rotuloDoSimbolo = page.getByTestId('exposicao-' + simboloDaExposicao).locator('dt', { hasText: rotuloDaExposicao });
        await expect(rotuloDoSimbolo).toHaveCount(1);
        // Uma linha mede menos que duas vezes o tamanho da letra; quebrado em duas, passa disso.
        const cabeEmUmaLinha = await rotuloDoSimbolo.evaluate(
          (rotuloNaPagina) => rotuloNaPagina.getBoundingClientRect().height < parseFloat(getComputedStyle(rotuloNaPagina).fontSize) * 2,
        );
        expect(cabeEmUmaLinha, `${simboloDaExposicao} / ${rotuloDaExposicao}`).toBe(true);
      }
    }
  });
}

test('RNF-02: as fontes Sora e Manrope do tema carregam e os números usam algarismos tabulares', async ({ page }) => {
  await page.goto('/');
  const fontesCarregadas = await page.evaluate(async () => {
    await document.fonts.ready;
    return [...document.fonts]
      .filter((fonteCarregada) => fonteCarregada.status === 'loaded')
      .map((fonteCarregada) => `${fonteCarregada.family.replaceAll('"', '')} ${fonteCarregada.weight}`);
  });
  expect(fontesCarregadas).toContain('Sora 600');
  expect(fontesCarregadas).toContain('Manrope 400');
  await expect(page.getByRole('heading', { name: 'Boleta de ordens' })).toHaveCSS('font-family', /^Sora/);
  await expect(page.locator('body')).toHaveCSS('font-family', /^Manrope/);
  await expect(page.getByTestId('total-estimado')).toHaveCSS('font-variant-numeric', 'tabular-nums');
});
