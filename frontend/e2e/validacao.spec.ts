import { expect, test, type Page } from '@playwright/test';
import { ROTA_DE_CRIACAO_DE_ORDEM } from '../src/ordensService';

// Conta toda requisição de criação de ordem que sair da página, para provar
// que os casos inválidos são barrados antes de chegar ao servidor (CA-15).
function contarEnviosDeOrdem(page: Page) {
  const enviosDeOrdem: string[] = [];
  page.on('request', (requisicaoDaPagina) => {
    if (requisicaoDaPagina.method() === 'POST' && new URL(requisicaoDaPagina.url()).pathname === ROTA_DE_CRIACAO_DE_ORDEM) {
      enviosDeOrdem.push(requisicaoDaPagina.url());
    }
  });
  return enviosDeOrdem;
}

async function preencherBoleta(page: Page, quantidade: string, preco: string) {
  await page.getByLabel(/^Quantidade de/).fill(quantidade);
  await page.getByLabel('Preço por ação (R$)').fill(preco);
}

test.beforeEach(async ({ page }) => {
  await page.goto('/');
});

test('CA-1: o símbolo só oferece PETR4, VALE3 e VIIA4', async ({ page }) => {
  const opcoesDeSimbolo = page.getByLabel('Símbolo').locator('option');
  await expect(opcoesDeSimbolo).toHaveText(['PETR4', 'VALE3', 'VIIA4']);
});

test('CA-2: o lado só oferece Compra e Venda', async ({ page }) => {
  const alternadorDeLado = page.getByRole('group', { name: 'Lado da ordem' });
  await expect(alternadorDeLado.getByRole('button')).toHaveText(['Compra', 'Venda']);
  await alternadorDeLado.getByRole('button', { name: 'Venda' }).click();
  await expect(alternadorDeLado.getByRole('button', { name: 'Venda' })).toHaveAttribute('aria-pressed', 'true');
  await expect(alternadorDeLado.getByRole('button', { name: 'Compra' })).toHaveAttribute('aria-pressed', 'false');
});

const quantidadesRecusadas: Array<[string, string]> = [
  ['0', 'A quantidade deve ser maior que zero.'],
  ['-5', 'A quantidade deve ser maior que zero.'],
  ['1,5', 'A quantidade deve ser um número inteiro.'],
  ['abc', 'A quantidade deve ser um número inteiro.'],
  ['100.000', 'A quantidade deve ser menor que 100.000.'],
];

for (const [quantidade, mensagemEsperada] of quantidadesRecusadas) {
  test(`CA-3/CA-15: quantidade "${quantidade}" é recusada na tela, sem envio`, async ({ page }) => {
    const enviosDeOrdem = contarEnviosDeOrdem(page);
    await preencherBoleta(page, quantidade, '10,00');
    await page.getByRole('button', { name: /^Enviar ordem/ }).click();
    await expect(page.locator('#erro-quantidade')).toHaveText(mensagemEsperada);
    await expect(page.getByLabel(/^Quantidade de/)).toHaveAttribute('aria-invalid', 'true');
    expect(enviosDeOrdem).toEqual([]);
  });
}

const precosRecusados: Array<[string, string]> = [
  ['0', 'O preço deve ser maior que zero.'],
  ['-1', 'O preço deve ser maior que zero.'],
  ['1.000', 'O preço deve ser menor que 1.000,00.'],
  ['10,005', 'O preço deve ser múltiplo de 0,01.'],
  ['abc', 'O preço deve ser um número.'],
];

for (const [preco, mensagemEsperada] of precosRecusados) {
  test(`CA-4/CA-15: preço "${preco}" é recusado na tela, sem envio`, async ({ page }) => {
    const enviosDeOrdem = contarEnviosDeOrdem(page);
    await preencherBoleta(page, '10', preco);
    await page.getByRole('button', { name: /^Enviar ordem/ }).click();
    await expect(page.locator('#erro-preco')).toHaveText(mensagemEsperada);
    await expect(page.getByLabel('Preço por ação (R$)')).toHaveAttribute('aria-invalid', 'true');
    expect(enviosDeOrdem).toEqual([]);
  });
}

test('CA-3/CA-4: limites válidos (1 e 99.999; 0,01 e 999,99) não mostram erro', async ({ page }) => {
  for (const [quantidade, preco, totalEsperado] of [
    ['1', '0,01', 'R$ 0,01'],
    ['99.999', '999,99', 'R$ 99.998.000,01'],
  ]) {
    await preencherBoleta(page, quantidade, preco);
    await expect(page.getByTestId('total-estimado')).toHaveText(totalEsperado);
    await expect(page.locator('#erro-quantidade')).toHaveCount(0);
    await expect(page.locator('#erro-preco')).toHaveCount(0);
  }
});
