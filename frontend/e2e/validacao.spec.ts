import { expect, test, type Page } from '@playwright/test';
import { ROTA_DE_CRIACAO_DE_ORDEM } from '../src/ordensService';

// Conta toda requisição de criação de ordem que sair da página, para provar
// que os casos inválidos são barrados antes de chegar ao servidor (CA-15).
function contarEnviosDeOrdem(paginaDaBoleta: Page) {
  const enviosDeOrdem: string[] = [];
  paginaDaBoleta.on('request', (requisicaoDaPagina) => {
    if (requisicaoDaPagina.method() === 'POST' && new URL(requisicaoDaPagina.url()).pathname === ROTA_DE_CRIACAO_DE_ORDEM) {
      enviosDeOrdem.push(requisicaoDaPagina.url());
    }
  });
  return enviosDeOrdem;
}

async function preencherBoleta(paginaDaBoleta: Page, quantidadeDigitada: string, precoDigitado: string) {
  await paginaDaBoleta.getByLabel(/^Quantidade de/).fill(quantidadeDigitada);
  await paginaDaBoleta.getByLabel('Preço por ação (R$)').fill(precoDigitado);
}

function boletaDaPagina(paginaDaBoleta: Page) {
  return paginaDaBoleta.getByRole('form', { name: 'Boleta de ordem' });
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
  ['-1', 'A quantidade deve ser maior que zero.'],
  ['1,5', 'A quantidade deve ser um número inteiro.'],
  ['abc', 'A quantidade deve ser um número inteiro.'],
  ['100.000', 'A quantidade deve ser menor que 100.000.'],
];

for (const [quantidadeRecusada, mensagemEsperada] of quantidadesRecusadas) {
  test(`CA-3/CA-15: quantidade "${quantidadeRecusada}" é recusada na tela, sem envio`, async ({ page }) => {
    const enviosDeOrdem = contarEnviosDeOrdem(page);
    await preencherBoleta(page, quantidadeRecusada, '10,00');
    await page.getByRole('button', { name: /^Enviar ordem/ }).click();
    const alertaDaBoleta = boletaDaPagina(page).getByRole('alert');
    await expect(alertaDaBoleta).toHaveCount(1);
    await expect(alertaDaBoleta).toHaveText(mensagemEsperada);
    await expect(page.getByLabel(/^Quantidade de/)).toHaveAttribute('aria-invalid', 'true');
    await expect(page.getByLabel(/^Quantidade de/)).toHaveAttribute('aria-describedby', 'erro-quantidade');
    await expect(alertaDaBoleta).toHaveAttribute('id', 'erro-quantidade');
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

for (const [precoRecusado, mensagemEsperada] of precosRecusados) {
  test(`CA-4/CA-15: preço "${precoRecusado}" é recusado na tela, sem envio`, async ({ page }) => {
    const enviosDeOrdem = contarEnviosDeOrdem(page);
    await preencherBoleta(page, '10', precoRecusado);
    await page.getByRole('button', { name: /^Enviar ordem/ }).click();
    const alertaDaBoleta = boletaDaPagina(page).getByRole('alert');
    await expect(alertaDaBoleta).toHaveCount(1);
    await expect(alertaDaBoleta).toHaveText(mensagemEsperada);
    await expect(page.getByLabel('Preço por ação (R$)')).toHaveAttribute('aria-invalid', 'true');
    await expect(page.getByLabel('Preço por ação (R$)')).toHaveAttribute('aria-describedby', 'erro-preco');
    await expect(alertaDaBoleta).toHaveAttribute('id', 'erro-preco');
    expect(enviosDeOrdem).toEqual([]);
  });
}

test('RF-14: quantidade e preço vazios são recusados na tela, cada um com sua mensagem, sem envio', async ({ page }) => {
  const enviosDeOrdem = contarEnviosDeOrdem(page);
  await preencherBoleta(page, '', '');
  await page.getByRole('button', { name: /^Enviar ordem/ }).click();
  await expect(boletaDaPagina(page).getByRole('alert')).toHaveText(['Informe a quantidade.', 'Informe o preço.']);
  expect(enviosDeOrdem).toEqual([]);
});

test('CA-3/CA-4: limites válidos (1 e 99.999; 0,01 e 999,99) viram valor aceito no total estimado', async ({ page }) => {
  for (const [quantidadeValida, precoValido, totalEsperado] of [
    ['1', '0,01', 'R$ 0,01'],
    ['99.999', '999,99', 'R$ 99.998.000,01'],
  ]) {
    await preencherBoleta(page, quantidadeValida, precoValido);
    await expect(page.getByTestId('total-estimado')).toHaveText(totalEsperado);
  }
});

const passosDaQuantidade: Array<{ quantidadeDigitadaAntes: string; nomeDoBotao: string; quantidadeEsperada: string }> = [
  { quantidadeDigitadaAntes: '100', nomeDoBotao: 'Aumentar quantidade', quantidadeEsperada: '101' },
  { quantidadeDigitadaAntes: '100', nomeDoBotao: 'Diminuir quantidade', quantidadeEsperada: '99' },
  { quantidadeDigitadaAntes: '1', nomeDoBotao: 'Diminuir quantidade', quantidadeEsperada: '1' },
  { quantidadeDigitadaAntes: '99.999', nomeDoBotao: 'Aumentar quantidade', quantidadeEsperada: '99999' },
  { quantidadeDigitadaAntes: '100.000', nomeDoBotao: 'Diminuir quantidade', quantidadeEsperada: '99999' },
  { quantidadeDigitadaAntes: 'abc', nomeDoBotao: 'Aumentar quantidade', quantidadeEsperada: '1' },
];

for (const { quantidadeDigitadaAntes, nomeDoBotao, quantidadeEsperada } of passosDaQuantidade) {
  test(`RF-16: com "${quantidadeDigitadaAntes}" digitado, "${nomeDoBotao}" leva a ${quantidadeEsperada}, sem sair da faixa`, async ({ page }) => {
    await page.getByLabel(/^Quantidade de/).fill(quantidadeDigitadaAntes);
    await page.getByRole('button', { name: nomeDoBotao }).click();
    await expect(page.getByLabel(/^Quantidade de/)).toHaveValue(quantidadeEsperada);
  });
}
