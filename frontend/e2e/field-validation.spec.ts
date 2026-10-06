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
  ['', 'Informe a quantidade.'],
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

// With the bank-style mask the only price left to refuse on screen is 0,00 (empty also becomes 0,00).
const refusedPrices: Array<[string, string]> = [
  ['0,00', 'O preço deve ser maior que zero.'],
  ['', 'O preço deve ser maior que zero.'],
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
  await expect(locateOrderTicketForm(page).getByRole('alert')).toHaveText(['Informe a quantidade.', 'O preço deve ser maior que zero.']);
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
  { quantityTypedBefore: '99998', buttonName: 'Aumentar quantidade', expectedQuantity: '99999' },
  { quantityTypedBefore: '', buttonName: 'Aumentar quantidade', expectedQuantity: '1' },
];

for (const { quantityTypedBefore, buttonName, expectedQuantity } of quantitySteps) {
  test(`RF-16: with "${quantityTypedBefore}" typed, "${buttonName}" leads to ${expectedQuantity}, without leaving the range`, async ({ page }) => {
    await page.getByLabel(/^Quantidade de/).fill(quantityTypedBefore);
    await page.getByRole('button', { name: buttonName }).click();
    await expect(page.getByLabel(/^Quantidade de/)).toHaveValue(expectedQuantity);
  });
}

function locateSummaryPrice(orderTicketPage: Page) {
  return locateOrderTicketForm(orderTicketPage).locator('.order-summary-row').filter({ hasText: 'Preço por ação' }).locator('dd');
}

test('CA-1 (F5): typing 9999999 leaves the quantity at 99999, and the + is disabled and does not change it', async ({ page }) => {
  const quantityField = page.getByLabel(/^Quantidade de/);
  const increaseQuantityButton = page.getByRole('button', { name: 'Aumentar quantidade' });
  await quantityField.fill('');
  await quantityField.pressSequentially('9999999');
  await expect(quantityField).toHaveValue('99999');
  await expect(increaseQuantityButton).toBeDisabled();
  await increaseQuantityButton.click({ force: true });
  await expect(quantityField).toHaveValue('99999');
});

test('CA-1 (F5): below 99999 the + stays enabled, and reaching 99999 with it disables it', async ({ page }) => {
  const increaseQuantityButton = page.getByRole('button', { name: 'Aumentar quantidade' });
  await page.getByLabel(/^Quantidade de/).fill('99998');
  await expect(increaseQuantityButton).toBeEnabled();
  await increaseQuantityButton.click();
  await expect(page.getByLabel(/^Quantidade de/)).toHaveValue('99999');
  await expect(increaseQuantityButton).toBeDisabled();
});

test('CA-1 (F5): letters, comma, dot and minus never get into the quantity', async ({ page }) => {
  const quantityField = page.getByLabel(/^Quantidade de/);
  await quantityField.fill('');
  await quantityField.pressSequentially('-1,5a.0');
  await expect(quantityField).toHaveValue('150');
});

test('CA-2 (F5): the price starts at 0,00 and 2, 5, 0, 0 fills from the right up to 25,00, with the summary following', async ({ page }) => {
  const priceField = page.getByLabel('Preço por ação (R$)');
  await expect(priceField).toHaveValue('0,00');
  await expect(locateSummaryPrice(page)).toHaveText('R$ 0,00');
  await priceField.press('End');
  for (const [typedPriceKey, expectedShownPrice] of [['2', '0,02'], ['5', '0,25'], ['0', '2,50'], ['0', '25,00']]) {
    await priceField.press(typedPriceKey);
    await expect(priceField, `after ${typedPriceKey}`).toHaveValue(expectedShownPrice);
  }
  await expect(locateSummaryPrice(page)).toHaveText('R$ 25,00');
  await expect(page.getByTestId('total-estimado')).toHaveText('R$ 2.500,00');
  await page.getByLabel(/^Quantidade de/).fill('99999');
  await expect(page.getByTestId('total-estimado')).toHaveText('R$ 2.499.975,00');
});

test('CA-2 (F5): six nines stop the price at 999,99 and the next digit does not get in', async ({ page }) => {
  const priceField = page.getByLabel('Preço por ação (R$)');
  await priceField.press('End');
  await priceField.pressSequentially('999999');
  await expect(priceField).toHaveValue('999,99');
  await priceField.press('9');
  await expect(priceField).toHaveValue('999,99');
  await expect(locateSummaryPrice(page)).toHaveText('R$ 999,99');
});

test('CA-2 (F5): Backspace moves the cents back to the right', async ({ page }) => {
  const priceField = page.getByLabel('Preço por ação (R$)');
  await priceField.press('End');
  await priceField.pressSequentially('2500');
  await priceField.press('Backspace');
  await expect(priceField).toHaveValue('2,50');
});
