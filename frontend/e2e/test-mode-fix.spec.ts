import { expect, test, type Page, type Request } from '@playwright/test';
import path from 'node:path';
import { CREATE_ORDER_ROUTE, EXPOSURES_ROUTE, ORDERS_ROUTE } from '../src/services/ordersService';

// Talks to the real OrderGenerator and OrderAccumulator: the test mode order goes through FIX and the
// OrderAccumulator is the one that rejects it and stores the reason (CA-8). Nothing here is intercepted.

const EVIDENCE_FOLDER = process.env.ORDER_LIST_EVIDENCE_FOLDER ?? path.resolve('test-results', 'order-list-evidence');
const THREE_FIELD_REJECT_REASON = 'Símbolo inválido. Use PETR4, VALE3 ou VIIA4. A quantidade deve ser um número inteiro. O preço deve ser múltiplo de 0,01.';

async function deleteAllOrdersOnServer(orderTicketPage: Page) {
  expect((await orderTicketPage.request.delete(ORDERS_ROUTE)).status()).toBe(204);
}

function locateOrderTicketForm(orderTicketPage: Page) {
  return orderTicketPage.getByRole('form', { name: 'Boleta de ordem' });
}

function locateOrderListCard(orderTicketPage: Page) {
  return orderTicketPage.getByRole('region', { name: 'Compra/Venda' });
}

function locateTopOrderRowCell(orderTicketPage: Page, rowColumn: string) {
  return locateOrderListCard(orderTicketPage).getByTestId('linha-da-ordem').first().locator(`td[data-column="${rowColumn}"]`);
}

// After every send the screen reads exposure and list page 1 again: wait for both, so each check reads the new screen.
async function sendOrderAndWaitForReread(orderTicketPage: Page) {
  const isGetOf = (sentRequest: Request, readPath: string) => sentRequest.method() === 'GET' && new URL(sentRequest.url()).pathname === readPath;
  const orderCreationRequest = orderTicketPage.waitForRequest((sentRequest) => sentRequest.method() === 'POST' && new URL(sentRequest.url()).pathname === CREATE_ORDER_ROUTE);
  const rereadResponses = Promise.all([
    orderTicketPage.waitForResponse((httpResponse) => isGetOf(httpResponse.request(), EXPOSURES_ROUTE)),
    orderTicketPage.waitForResponse((httpResponse) => isGetOf(httpResponse.request(), ORDERS_ROUTE)),
  ]);
  await locateOrderTicketForm(orderTicketPage).getByRole('button', { name: /^Enviar ordem/ }).click();
  const sentOrderRequest = await orderCreationRequest;
  await rereadResponses;
  return sentOrderRequest;
}

// No cleanup after each test on purpose: the CI stops the OrderAccumulator right after this suite, and
// without-accumulator.spec.ts needs stored orders with exposure; the last test here leaves PETR4 100 × 10,00 accepted.
test.beforeEach(async ({ page }) => {
  await deleteAllOrdersOnServer(page);
});

test('CA-8 and CA-10: in test mode ITUB4 / 1,5 / 10,005 goes through FIX, comes back "Rejeitada" with the three reasons and enters the list as sent', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/');
  const orderTicketForm = locateOrderTicketForm(page);
  await orderTicketForm.getByRole('group', { name: 'Símbolo' }).getByRole('button', { name: 'PETR4', exact: true }).dblclick();
  await expect(orderTicketForm.getByText('Modo de teste', { exact: true })).toBeVisible();
  await orderTicketForm.getByRole('group', { name: 'Lado da ordem' }).getByRole('button', { name: 'Compra' }).click();
  await orderTicketForm.getByRole('textbox', { name: 'Símbolo', exact: true }).fill('ITUB4');
  await orderTicketForm.getByLabel('Quantidade', { exact: true }).fill('1,5');
  await orderTicketForm.getByLabel('Preço por ação (R$)').fill('10,005');

  const sentOrderRequest = await sendOrderAndWaitForReread(page);
  // Decision 15: the test mode sends the typed text, only with the decimal comma turned into a dot.
  expect(sentOrderRequest.postDataJSON()).toEqual({ symbol: 'ITUB4', side: 'buy', quantity: '1.5', price: '10.005' });
  const sentOrderAnswer = (await (await sentOrderRequest.response())!.json()) as { data: { status: string; clOrdId: string } };
  expect(sentOrderAnswer.data.status).toBe('rejected');

  const responseBox = locateOrderListCard(page).getByTestId('caixa-de-resposta');
  await expect(responseBox.getByTestId('status-da-ordem')).toHaveText('Rejeitada');
  await expect(responseBox.getByTestId('mensagem-da-ordem')).toHaveText(THREE_FIELD_REJECT_REASON);
  await expect(responseBox.getByTestId('ordem-da-resposta')).toHaveText('ITUB4 · Compra · 1,5 × R$ 10,005');

  await expect(locateTopOrderRowCell(page, 'send-identifier')).toHaveText(sentOrderAnswer.data.clOrdId);
  await expect(locateTopOrderRowCell(page, 'status').locator('.order-badge')).toHaveText('Rejeitada');
  await expect(locateTopOrderRowCell(page, 'reject-reason')).toHaveText(THREE_FIELD_REJECT_REASON);
  await expect(locateTopOrderRowCell(page, 'asset')).toHaveText('ITUB4');
  await expect(locateTopOrderRowCell(page, 'quantity')).toHaveText('1,5');
  await expect(locateTopOrderRowCell(page, 'price')).toHaveText('R$ 10,005');

  const storedOrders = ((await (await page.request.get(ORDERS_ROUTE + '?page=1')).json()) as { data: { orders: Array<{ clOrdId: string; rejectReason: string | null; price: number }> } }).data.orders;
  expect(storedOrders).toHaveLength(1);
  expect(storedOrders[0]).toMatchObject({ clOrdId: sentOrderAnswer.data.clOrdId, rejectReason: THREE_FIELD_REJECT_REASON, price: 10.005 });

  await page.mouse.move(0, 0);
  await page.screenshot({ path: path.join(EVIDENCE_FOLDER, '07-modo-teste-rejeitada-1440.png'), fullPage: true });
  await locateOrderListCard(page).getByTestId('linha-da-ordem').first().screenshot({ path: path.join(EVIDENCE_FOLDER, '07-linha-10-005-1440.png') });
});

test('CA-23: with the test mode off, PETR4 Compra 100 × 10,00 comes back "Aceita", adds R$ 1.000,00 to the exposure and the row has no reason', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/');
  const orderTicketForm = locateOrderTicketForm(page);
  await expect(orderTicketForm.getByText('Modo de teste', { exact: true })).toHaveCount(0);
  await expect(page.getByTestId('exposicao-PETR4').getByTestId('exposicao-atual')).toHaveText('R$ 0,00');
  await orderTicketForm.getByRole('group', { name: 'Símbolo' }).getByRole('button', { name: 'PETR4', exact: true }).click();
  await orderTicketForm.getByRole('group', { name: 'Lado da ordem' }).getByRole('button', { name: 'Compra' }).click();
  await orderTicketForm.getByLabel(/^Quantidade de/).fill('100');
  await orderTicketForm.getByLabel('Preço por ação (R$)').fill('10,00');

  const sentOrderRequest = await sendOrderAndWaitForReread(page);
  expect(sentOrderRequest.postDataJSON()).toEqual({ symbol: 'PETR4', side: 'buy', quantity: 100, price: 10 });
  const responseBox = locateOrderListCard(page).getByTestId('caixa-de-resposta');
  await expect(responseBox.getByTestId('status-da-ordem')).toHaveText('Aceita');
  await expect(responseBox.getByTestId('ordem-da-resposta')).toHaveText('PETR4 · Compra · 100 × R$ 10,00');
  await expect(page.getByTestId('exposicao-PETR4').getByTestId('exposicao-atual')).toHaveText('R$ 1.000,00');
  await expect(locateTopOrderRowCell(page, 'status').locator('.order-badge')).toHaveText('Aceita');
  await expect(locateTopOrderRowCell(page, 'reject-reason')).toHaveText('—');
  await expect(locateTopOrderRowCell(page, 'price')).toHaveText('R$ 10,00');
  await page.mouse.move(0, 0);
  await page.screenshot({ path: path.join(EVIDENCE_FOLDER, '07-caixa-aceita-1440.png'), fullPage: true });
});
