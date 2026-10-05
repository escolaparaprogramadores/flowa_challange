import { expect, test, type Page } from '@playwright/test';
import { CREATE_ORDER_ROUTE } from '../src/services/ordersService';

// Counts every order creation request that leaves the page, to prove
// that invalid cases are blocked before reaching the server (CA-15).
function countOrderSends(orderTicketPage: Page) {
  const orderSends: string[] = [];
  orderTicketPage.on('request', (pageRequest) => {
    if (pageRequest.method() === 'POST' && new URL(pageRequest.url()).pathname === CREATE_ORDER_ROUTE) {
      orderSends.push(pageRequest.url());
    }
  });
  return orderSends;
}

async function fillOrderTicket(orderTicketPage: Page, typedQuantity: string, typedPrice: string) {
  await orderTicketPage.getByLabel(/^Quantidade de/).fill(typedQuantity);
  await orderTicketPage.getByLabel('Preço por ação (R$)').fill(typedPrice);
}

function locateOrderTicketForm(orderTicketPage: Page) {
  return orderTicketPage.getByRole('form', { name: 'Boleta de ordem' });
}

test.beforeEach(async ({ page }) => {
  await page.goto('/');
});

test('CA-1: the symbol only offers PETR4, VALE3 and VIIA4', async ({ page }) => {
  const symbolButtons = page.getByRole('group', { name: 'Símbolo' }).getByRole('button');
  await expect(symbolButtons).toHaveText(['PETR4', 'VALE3', 'VIIA4']);
});

test('CA-2: the side only offers Compra and Venda', async ({ page }) => {
  const sideToggle = page.getByRole('group', { name: 'Lado da ordem' });
  await expect(sideToggle.getByRole('button')).toHaveText(['Compra', 'Venda']);
  await sideToggle.getByRole('button', { name: 'Venda' }).click();
  await expect(sideToggle.getByRole('button', { name: 'Venda' })).toHaveAttribute('aria-pressed', 'true');
  await expect(sideToggle.getByRole('button', { name: 'Compra' })).toHaveAttribute('aria-pressed', 'false');
});

const refusedQuantities: Array<[string, string]> = [
  ['0', 'A quantidade deve ser maior que zero.'],
  ['-1', 'A quantidade deve ser maior que zero.'],
  ['1,5', 'A quantidade deve ser um número inteiro.'],
  ['abc', 'A quantidade deve ser um número inteiro.'],
  ['100.000', 'A quantidade deve ser menor que 100.000.'],
];

for (const [refusedQuantity, expectedMessage] of refusedQuantities) {
  test(`CA-3/CA-15: quantity "${refusedQuantity}" is refused on screen, without sending`, async ({ page }) => {
    const orderSends = countOrderSends(page);
    await fillOrderTicket(page, refusedQuantity, '10,00');
    await page.getByRole('button', { name: /^Enviar ordem/ }).click();
    const orderTicketAlert = locateOrderTicketForm(page).getByRole('alert');
    await expect(orderTicketAlert).toHaveCount(1);
    await expect(orderTicketAlert).toHaveText(expectedMessage);
    await expect(page.getByLabel(/^Quantidade de/)).toHaveAttribute('aria-invalid', 'true');
    await expect(page.getByLabel(/^Quantidade de/)).toHaveAttribute('aria-describedby', 'quantity-error');
    await expect(orderTicketAlert).toHaveAttribute('id', 'quantity-error');
    expect(orderSends).toEqual([]);
  });
}

const refusedPrices: Array<[string, string]> = [
  ['0', 'O preço deve ser maior que zero.'],
  ['-1', 'O preço deve ser maior que zero.'],
  ['1.000', 'O preço deve ser menor que 1.000,00.'],
  ['10,005', 'O preço deve ser múltiplo de 0,01.'],
  ['abc', 'O preço deve ser um número.'],
];

for (const [refusedPrice, expectedMessage] of refusedPrices) {
  test(`CA-4/CA-15: price "${refusedPrice}" is refused on screen, without sending`, async ({ page }) => {
    const orderSends = countOrderSends(page);
    await fillOrderTicket(page, '10', refusedPrice);
    await page.getByRole('button', { name: /^Enviar ordem/ }).click();
    const orderTicketAlert = locateOrderTicketForm(page).getByRole('alert');
    await expect(orderTicketAlert).toHaveCount(1);
    await expect(orderTicketAlert).toHaveText(expectedMessage);
    await expect(page.getByLabel('Preço por ação (R$)')).toHaveAttribute('aria-invalid', 'true');
    await expect(page.getByLabel('Preço por ação (R$)')).toHaveAttribute('aria-describedby', 'price-error');
    await expect(orderTicketAlert).toHaveAttribute('id', 'price-error');
    expect(orderSends).toEqual([]);
  });
}

test('RF-14: empty quantity and price are refused on screen, each with its message, without sending', async ({ page }) => {
  const orderSends = countOrderSends(page);
  await fillOrderTicket(page, '', '');
  await page.getByRole('button', { name: /^Enviar ordem/ }).click();
  await expect(locateOrderTicketForm(page).getByRole('alert')).toHaveText(['Informe a quantidade.', 'Informe o preço.']);
  expect(orderSends).toEqual([]);
});

test('CA-3/CA-4: valid limits (1 and 99.999; 0,01 and 999,99) become an accepted value in the estimated total', async ({ page }) => {
  for (const [validQuantity, validPrice, expectedTotal] of [
    ['1', '0,01', 'R$ 0,01'],
    ['99.999', '999,99', 'R$ 99.998.000,01'],
  ]) {
    await fillOrderTicket(page, validQuantity, validPrice);
    await expect(page.getByTestId('total-estimado')).toHaveText(expectedTotal);
  }
});

const quantitySteps: Array<{ quantityTypedBefore: string; buttonName: string; expectedQuantity: string }> = [
  { quantityTypedBefore: '100', buttonName: 'Aumentar quantidade', expectedQuantity: '101' },
  { quantityTypedBefore: '100', buttonName: 'Diminuir quantidade', expectedQuantity: '99' },
  { quantityTypedBefore: '1', buttonName: 'Diminuir quantidade', expectedQuantity: '1' },
  { quantityTypedBefore: '99.999', buttonName: 'Aumentar quantidade', expectedQuantity: '99999' },
  { quantityTypedBefore: '100.000', buttonName: 'Diminuir quantidade', expectedQuantity: '99999' },
  { quantityTypedBefore: 'abc', buttonName: 'Aumentar quantidade', expectedQuantity: '1' },
];

for (const { quantityTypedBefore, buttonName, expectedQuantity } of quantitySteps) {
  test(`RF-16: with "${quantityTypedBefore}" typed, "${buttonName}" leads to ${expectedQuantity}, without leaving the range`, async ({ page }) => {
    await page.getByLabel(/^Quantidade de/).fill(quantityTypedBefore);
    await page.getByRole('button', { name: buttonName }).click();
    await expect(page.getByLabel(/^Quantidade de/)).toHaveValue(expectedQuantity);
  });
}
