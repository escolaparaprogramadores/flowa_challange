import { expect, test, type Page } from '@playwright/test';

// Quebras do tema Base (max-width inclui o próprio valor): uma coluna até 860 px,
// duas de 861 a 1180 px e três a partir de 1181 px. Cada fronteira é medida dos dois lados.
const LARGURAS_DO_TEMA = [
  { largura: 390, colunasEsperadas: 1 },
  { largura: 860, colunasEsperadas: 1 },
  { largura: 861, colunasEsperadas: 2 },
  { largura: 1180, colunasEsperadas: 2 },
  { largura: 1181, colunasEsperadas: 3 },
  { largura: 1280, colunasEsperadas: 3 },
];

async function contarColunasDaGrade(page: Page) {
  return page.locator('.grade').evaluate((grade) => getComputedStyle(grade).gridTemplateColumns.split(' ').length);
}

for (const { largura, colunasEsperadas } of LARGURAS_DO_TEMA) {
  test(`RNF-03: em ${largura} px a página não rola para o lado e a grade tem ${colunasEsperadas} coluna(s)`, async ({ page }) => {
    await page.setViewportSize({ width: largura, height: 900 });
    await page.goto('/');
    await expect(page.getByRole('heading', { name: 'Boleta de ordens' })).toBeVisible();
    const larguraDoConteudo = await page.evaluate(() => document.documentElement.scrollWidth);
    expect(larguraDoConteudo).toBe(largura);
    expect(await contarColunasDaGrade(page)).toBe(colunasEsperadas);
  });
}

test('ASSUMI-05: no celular a boleta vem primeiro, depois a resposta e por último a exposição', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 900 });
  await page.goto('/');
  const topoDaBoleta = (await page.getByRole('form', { name: 'Boleta de ordem' }).boundingBox())!.y;
  const topoDaResposta = (await page.getByRole('heading', { name: 'Resposta da ordem' }).boundingBox())!.y;
  const topoDaExposicao = (await page.getByRole('heading', { name: 'Exposição por ativo' }).boundingBox())!.y;
  expect(topoDaBoleta).toBeLessThan(topoDaResposta);
  expect(topoDaResposta).toBeLessThan(topoDaExposicao);
});

test('RNF-02: as fontes Sora e Manrope do tema carregam e os números usam algarismos tabulares', async ({ page }) => {
  await page.goto('/');
  const fontesCarregadas = await page.evaluate(async () => {
    await document.fonts.ready;
    return [...document.fonts]
      .filter((fonte) => fonte.status === 'loaded')
      .map((fonte) => `${fonte.family.replaceAll('"', '')} ${fonte.weight}`);
  });
  expect(fontesCarregadas).toContain('Sora 600');
  expect(fontesCarregadas).toContain('Manrope 400');
  await expect(page.getByRole('heading', { name: 'Boleta de ordens' })).toHaveCSS('font-family', /^Sora/);
  await expect(page.locator('body')).toHaveCSS('font-family', /^Manrope/);
  await expect(page.getByTestId('total-estimado')).toHaveCSS('font-variant-numeric', 'tabular-nums');
});
