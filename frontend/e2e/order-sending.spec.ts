import { expect, test, type Page } from '@playwright/test';
import { CREATE_ORDER_ROUTE, EXPOSURES_ROUTE, ORDERS_ROUTE } from '../src/services/ordersService';

// These scenarios talk to the real OrderGenerator and OrderAccumulator.
// The exposure values are read before and after each order, so the test
// does not depend on the database being empty.

const EXPOSURE_LIMIT_PER_SYMBOL = 100_000_000;
const ACCEPTED_STATUS_COLOR = 'rgb(111, 235, 192)';
const REJECTED_STATUS_COLOR = 'rgb(255, 164, 151)';

type TestOrder = { symbol: string; sideLabel: 'Compra' | 'Venda'; quantity: string; price: string };

type ServerCreatedOrder = { status: string; message: string; clOrdId: string };

// The answer of POST /api/orders is a DataMessage: the order is its "data" and the text of the screen is its "message".
function readCreatedOrderFromDataMessage(orderDataMessage: { message: string; data: { status: string; clOrdId: string } }): ServerCreatedOrder {
  return { ...orderDataMessage.data, message: orderDataMessage.message };
}

// Returns the POST body and only finishes after the list was read again, already with the sent order.
async function sendOrderThroughOrderTicket(orderTicketPage: Page, testOrder: TestOrder): Promise<ServerCreatedOrder> {
  await orderTicketPage.getByRole('group', { name: 'Símbolo' }).getByRole('button', { name: testOrder.symbol, exact: true }).click();
  await orderTicketPage.getByRole('group', { name: 'Lado da ordem' }).getByRole('button', { name: testOrder.sideLabel }).click();
  await orderTicketPage.getByLabel(/^Quantidade de/).fill(testOrder.quantity);
  await orderTicketPage.getByLabel('Preço por ação (R$)').fill(testOrder.price);
  const createOrderResponse = orderTicketPage.waitForResponse((httpResponse) => httpResponse.request().method() === 'POST' && new URL(httpResponse.url()).pathname === CREATE_ORDER_ROUTE);
  const orderListReread = orderTicketPage.waitForResponse((httpResponse) => httpResponse.request().method() === 'GET' && new URL(httpResponse.url()).pathname === ORDERS_ROUTE);
  await orderTicketPage.getByRole('button', { name: /^Enviar ordem/ }).click();
  const createdOrder = readCreatedOrderFromDataMessage(await (await createOrderResponse).json());
  await orderListReread;
  await expect(orderTicketPage.getByRole('button', { name: /^Enviar ordem/ })).toBeEnabled();
  return createdOrder;
}

type ServerExposure = { symbol: string; exposure: number; remaining: number };

async function readServerExposure(orderTicketPage: Page, exposureSymbol: string): Promise<ServerExposure> {
  const exposuresResponse = await orderTicketPage.request.get(EXPOSURES_ROUTE);
  expect(exposuresResponse.status()).toBe(200);
  const exposuresBody = ((await exposuresResponse.json()) as { data: { exposures: ServerExposure[] } }).data;
  const symbolExposure = exposuresBody.exposures.find((listedExposure) => listedExposure.symbol === exposureSymbol);
  if (!symbolExposure) throw new Error('Symbol ' + exposureSymbol + ' missing from /api/exposures');
  return symbolExposure;
}

const brazilianReaisFormatter = new Intl.NumberFormat('pt-BR', { style: 'currency', currency: 'BRL' });

async function expectExposurePanel(orderTicketPage: Page, exposureSymbol: string, currentExposure: number, remainingToLimit: number) {
  const symbolRow = orderTicketPage.getByTestId('exposicao-' + exposureSymbol);
  await expect(symbolRow.getByTestId('exposicao-atual')).toHaveText(brazilianReaisFormatter.format(currentExposure));
  await expect(symbolRow.getByTestId('exposicao-restante')).toHaveText(brazilianReaisFormatter.format(remainingToLimit));
}

// The answer of each send is now the new row at the top of "Compra/Venda" (CA-11, CA-12).
function locateTopOrderCell(orderTicketPage: Page, listColumn: string) {
  return orderTicketPage.getByTestId('linha-da-ordem').first().locator(`td[data-column="${listColumn}"]`);
}

function locateTopOrderBadge(orderTicketPage: Page) {
  return locateTopOrderCell(orderTicketPage, 'status').locator('.order-badge');
}

async function expectOrderAtTopOfList(
  orderTicketPage: Page,
  createdOrder: ServerCreatedOrder,
  expectedOrder: { badgeLabel: 'Aceita' | 'Rejeitada'; asset: string; sideLabel: string; quantity: string; price: string },
) {
  await expect(locateTopOrderCell(orderTicketPage, 'send-identifier')).toHaveText(createdOrder.clOrdId);
  await expect(locateTopOrderCell(orderTicketPage, 'send-identifier')).toHaveText(/^[0-9a-f]{32}$/i);
  await expect(locateTopOrderBadge(orderTicketPage)).toHaveText(expectedOrder.badgeLabel);
  await expect(locateTopOrderBadge(orderTicketPage)).toHaveCSS('color', expectedOrder.badgeLabel === 'Aceita' ? ACCEPTED_STATUS_COLOR : REJECTED_STATUS_COLOR);
  await expect(locateTopOrderCell(orderTicketPage, 'asset')).toHaveText(expectedOrder.asset);
  await expect(locateTopOrderCell(orderTicketPage, 'side')).toHaveText(expectedOrder.sideLabel);
  await expect(locateTopOrderCell(orderTicketPage, 'quantity')).toHaveText(expectedOrder.quantity);
  await expect(locateTopOrderCell(orderTicketPage, 'price')).toHaveText(expectedOrder.price);
}

test.beforeEach(async ({ page }) => {
  await page.goto('/');
});

test('CA-14 and CA-11: a valid buy is sent and enters accepted at the top of Compra/Venda', async ({ page }) => {
  const createdOrder = await sendOrderThroughOrderTicket(page, { symbol: 'PETR4', sideLabel: 'Compra', quantity: '1.000', price: '10,00' });
  expect(createdOrder.status).toBe('accepted');
  expect(createdOrder.message).toBe('Ordem aceita.');
  await expectOrderAtTopOfList(page, createdOrder, { badgeLabel: 'Aceita', asset: 'PETR4', sideLabel: 'Compra', quantity: '1.000', price: brazilianReaisFormatter.format(10) });
});

test('CA-14 and CA-11: a valid sell is sent and enters accepted at the top of Compra/Venda', async ({ page }) => {
  const createdOrder = await sendOrderThroughOrderTicket(page, { symbol: 'VALE3', sideLabel: 'Venda', quantity: '200', price: '55,30' });
  expect(createdOrder.status).toBe('accepted');
  await expectOrderAtTopOfList(page, createdOrder, { badgeLabel: 'Aceita', asset: 'VALE3', sideLabel: 'Venda', quantity: '200', price: brazilianReaisFormatter.format(55.3) });
});

test('CA-17: the panel shows the three assets and changes after an accepted order', async ({ page }) => {
  for (const orderSymbol of ['PETR4', 'VALE3', 'VIIA4']) {
    const currentExposure = await readServerExposure(page, orderSymbol);
    await expectExposurePanel(page, orderSymbol, currentExposure.exposure, currentExposure.remaining);
  }
  const exposureBefore = await readServerExposure(page, 'PETR4');
  const createdOrder = await sendOrderThroughOrderTicket(page, { symbol: 'PETR4', sideLabel: 'Compra', quantity: '1.000', price: '10,00' });
  expect(createdOrder.status).toBe('accepted');
  const expectedExposure = exposureBefore.exposure + 10_000;
  await expectExposurePanel(page, 'PETR4', expectedExposure, EXPOSURE_LIMIT_PER_SYMBOL - Math.abs(expectedExposure));
});

test('CA-16 and CA-17: an order that breaks the limit comes back rejected with the reason, enters "Rejeitada" at the top of the list and does not change the exposure', async ({ page }) => {
  // Takes VIIA4 close to the limit with valid, large orders; the first one that
  // no longer fits is the rejection the scenario wants to see.
  const exposureReads: string[] = [];
  page.on('request', (pageRequest) => {
    if (new URL(pageRequest.url()).pathname === EXPOSURES_ROUTE) exposureReads.push(pageRequest.url());
  });
  let wasRejected = false;
  for (let sendAttempt = 0; sendAttempt < 6 && !wasRejected; sendAttempt++) {
    const exposureBefore = await readServerExposure(page, 'VIIA4');
    const readsBeforeSending = exposureReads.length;
    const createdOrder = await sendOrderThroughOrderTicket(page, { symbol: 'VIIA4', sideLabel: 'Compra', quantity: '99.999', price: '999,99' });
    if (createdOrder.status === 'rejected') {
      wasRejected = true;
      expect(createdOrder.message).toBe('Ordem rejeitada: a exposição de VIIA4 passaria do limite de 100.000.000,00.');
      // The rejection is also stored: it enters the top of the list with the red badge (CA-11).
      await expectOrderAtTopOfList(page, createdOrder, { badgeLabel: 'Rejeitada', asset: 'VIIA4', sideLabel: 'Compra', quantity: '99.999', price: brazilianReaisFormatter.format(999.99) });
      // RF-31: after the rejection the screen reads the exposure again (one new read) and the value read did not change.
      await expect.poll(() => exposureReads.length).toBe(readsBeforeSending + 1);
      await expectExposurePanel(page, 'VIIA4', exposureBefore.exposure, exposureBefore.remaining);
      expect(await readServerExposure(page, 'VIIA4')).toEqual(exposureBefore);
    } else {
      expect(createdOrder.status).toBe('accepted');
      await expectOrderAtTopOfList(page, createdOrder, { badgeLabel: 'Aceita', asset: 'VIIA4', sideLabel: 'Compra', quantity: '99.999', price: brazilianReaisFormatter.format(999.99) });
    }
  }
  expect(wasRejected).toBe(true);
});

test('RF-24: while the order travels, sending is disabled and shows "Enviando…"', async ({ page }) => {
  // Holds the real request for a moment, without replacing the server answer.
  await page.route('**' + CREATE_ORDER_ROUTE, async (heldOrderCreation) => {
    await new Promise((releaseHeldRequest) => setTimeout(releaseHeldRequest, 800));
    await heldOrderCreation.continue();
  });
  await page.getByLabel(/^Quantidade de/).fill('10');
  await page.getByLabel('Preço por ação (R$)').fill('10,00');
  const createOrderResponse = page.waitForResponse((httpResponse) => httpResponse.request().method() === 'POST' && new URL(httpResponse.url()).pathname === CREATE_ORDER_ROUTE);
  await page.getByRole('button', { name: 'Enviar ordem de compra' }).click();
  const buttonWhileSending = page.getByRole('button', { name: 'Enviando…' });
  await expect(buttonWhileSending).toBeDisabled();
  const createdOrder = readCreatedOrderFromDataMessage(await (await createOrderResponse).json());
  await expect(locateTopOrderCell(page, 'send-identifier')).toHaveText(createdOrder.clOrdId);
  await expect(locateTopOrderBadge(page)).toHaveText('Aceita');
  await expect(page.getByRole('button', { name: 'Enviar ordem de compra' })).toBeEnabled();
});

test('RNF-08: the exposure is read on open and after each send, without continuous reading', async ({ context }) => {
  // New page, with the counter on before the first load: the one from beforeEach
  // may still have a read in flight and would count twice.
  const countedPage = await context.newPage();
  const exposureReads: string[] = [];
  countedPage.on('request', (pageRequest) => {
    if (new URL(pageRequest.url()).pathname === EXPOSURES_ROUTE) exposureReads.push(pageRequest.url());
  });
  await countedPage.goto('/');
  await expect(countedPage.getByTestId('exposicao-PETR4')).toBeVisible();
  await countedPage.waitForTimeout(3_000);
  expect(exposureReads).toHaveLength(1);
  const createdOrder = await sendOrderThroughOrderTicket(countedPage, { symbol: 'VALE3', sideLabel: 'Compra', quantity: '10', price: '10,00' });
  await expect(locateTopOrderCell(countedPage, 'send-identifier')).toHaveText(createdOrder.clOrdId);
  await expect(locateTopOrderBadge(countedPage)).toHaveText('Aceita');
  await countedPage.waitForTimeout(3_000);
  expect(exposureReads).toHaveLength(2);
});

test('regression: an old, slow exposure read does not erase the read made after sending', async ({ context, page }) => {
  const exposureBefore = await readServerExposure(page, 'PETR4');
  const pageWithSlowRead = await context.newPage();
  let firstReadAlreadyHeld = false;
  // The first read fetches the real value right away, but only delivers it 3 s later, already with the order accepted.
  await pageWithSlowRead.route('**' + EXPOSURES_ROUTE, async (exposureRead) => {
    if (firstReadAlreadyHeld) return exposureRead.continue();
    firstReadAlreadyHeld = true;
    const responseFromBeforeTheOrder = await exposureRead.fetch();
    await new Promise((releaseHeldRead) => setTimeout(releaseHeldRead, 3_000));
    await exposureRead.fulfill({ response: responseFromBeforeTheOrder });
  });
  await pageWithSlowRead.goto('/');
  const createdOrder = await sendOrderThroughOrderTicket(pageWithSlowRead, { symbol: 'PETR4', sideLabel: 'Compra', quantity: '1.000', price: '10,00' });
  expect(createdOrder.status).toBe('accepted');
  await pageWithSlowRead.waitForTimeout(3_500);
  const expectedExposure = exposureBefore.exposure + 10_000;
  await expectExposurePanel(pageWithSlowRead, 'PETR4', expectedExposure, EXPOSURE_LIMIT_PER_SYMBOL - Math.abs(expectedExposure));
});

test('RNF-02: every number on the screen uses tabular figures', async ({ page }) => {
  const createdOrder = await sendOrderThroughOrderTicket(page, { symbol: 'VALE3', sideLabel: 'Compra', quantity: '300', price: '12,34' });
  await expect(locateTopOrderCell(page, 'send-identifier')).toHaveText(createdOrder.clOrdId);
  const screenNumbers = [
    page.getByLabel(/^Quantidade de/),
    page.getByLabel('Preço por ação (R$)'),
    page.locator('.order-summary-row').filter({ hasText: 'Preço por ação' }).locator('dd'),
    page.getByTestId('total-estimado'),
    locateTopOrderCell(page, 'quantity'),
    locateTopOrderCell(page, 'price'),
    locateTopOrderCell(page, 'order-number'),
    locateTopOrderCell(page, 'send-identifier'),
    ...['PETR4', 'VALE3', 'VIIA4'].flatMap((panelSymbol) => [
      page.getByTestId('exposicao-' + panelSymbol).getByTestId('exposicao-atual'),
      page.getByTestId('exposicao-' + panelSymbol).getByTestId('exposicao-restante'),
    ]),
  ];
  for (const screenNumber of screenNumbers) {
    await expect(screenNumber).toHaveCount(1);
    await expect(screenNumber).toHaveCSS('font-variant-numeric', 'tabular-nums');
  }
});

test('RF-25 and CA-15: the server validation 400 shows in the Compra/Venda banner as "Não enviada", with the message of each field', async ({ page }) => {
  // The 400 only comes from an order the screen would already refuse; the answer below is the body of contract §1, row "Campo inválido".
  await page.route('**' + CREATE_ORDER_ROUTE, (orderCreation) =>
    orderCreation.fulfill({
      status: 400,
      contentType: 'application/problem+json',
      body: JSON.stringify({ type: 'urn:base-investimentos:problem:invalid-order', title: 'Dados inválidos', status: 400, detail: 'A ordem tem campos inválidos.', success: false, statusResultado: 'InvalidInput', errors: ['O preço deve ser múltiplo de 0,01.'] }),
    }),
  );
  await page.getByLabel(/^Quantidade de/).fill('10');
  await page.getByLabel('Preço por ação (R$)').fill('10,00');
  await page.getByRole('button', { name: 'Enviar ordem de compra' }).click();
  const sendFailureBanner = page.getByRole('region', { name: 'Compra/Venda' }).getByTestId('faixa-da-falha-no-envio');
  await expect(sendFailureBanner.getByTestId('status-da-ordem')).toHaveText('Não enviada');
  await expect(sendFailureBanner.getByTestId('status-da-ordem')).toHaveCSS('color', REJECTED_STATUS_COLOR);
  await expect(sendFailureBanner.getByTestId('mensagem-da-ordem')).toHaveText('A ordem tem campos inválidos.');
  await expect(sendFailureBanner.getByTestId('erros-de-campo-da-ordem').getByRole('listitem')).toHaveText(['O preço deve ser múltiplo de 0,01.']);
});
