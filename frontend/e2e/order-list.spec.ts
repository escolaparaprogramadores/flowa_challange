import { expect, test, type Locator, type Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { CREATE_ORDER_ROUTE, EXPOSURES_ROUTE, ORDERS_ROUTE } from '../src/services/ordersService';

// Talks to the real OrderGenerator and OrderAccumulator. The scenarios that need an empty database
// delete everything first through the contract's DELETE /api/orders route.

const EVIDENCE_FOLDER = process.env.ORDER_LIST_EVIDENCE_FOLDER ?? path.resolve('test-results', 'order-list-evidence');
const EMPTY_LIST_TEXT = 'Nenhuma ordem enviada ainda. Preencha a boleta e envie para ver a resposta aqui.';
const ORDER_LIST_COLUMNS = ['Data', 'Status', 'Motivo', 'Ativo', 'Lado', 'Quantidade', 'Preço', 'Número da ordem', 'Identificador do envio'];
const ACCEPTED_BADGE_COLORS = { background: 'rgba(79, 227, 176, 0.13)', text: 'rgb(111, 235, 192)' };
const REJECTED_BADGE_COLORS = { background: 'rgba(255, 138, 122, 0.12)', text: 'rgb(255, 164, 151)' };
const SENDING_BADGE_COLORS = { background: 'rgba(242, 184, 75, 0.14)', text: 'rgb(244, 197, 106)' };
const brazilianRealFormatter = new Intl.NumberFormat('pt-BR', { style: 'currency', currency: 'BRL' });
const LIMIT_REJECT_REASON = 'Ordem rejeitada: a exposição de PETR4 passaria do limite de 100.000.000,00.';
const PRICE_FIELD_REJECT_REASON = 'O preço deve ser múltiplo de 0,01.';

type OrderStoredOnServer = { receivedAt: string; status: string; symbol: string | null; side: string | null; quantity: number; price: number; orderId: string; clOrdId: string; rejectReason: string | null };
type OrderPageOnServer = { page: number; pageSize: number; total: number; orders: OrderStoredOnServer[] };
type TicketOrder = { side: 'Compra' | 'Venda'; quantity: string; price: string };

async function deleteAllOrdersOnServer(ticketPage: Page) {
  const deletionResponse = await ticketPage.request.delete(ORDERS_ROUTE);
  expect(deletionResponse.status()).toBe(204);
}

async function readFirstOrderPageOnServer(ticketPage: Page): Promise<OrderPageOnServer> {
  const orderListResponse = await ticketPage.request.get(ORDERS_ROUTE + '?page=1');
  expect(orderListResponse.status()).toBe(200);
  return ((await orderListResponse.json()) as { data: OrderPageOnServer }).data;
}

async function sendOrderThroughTicket(ticketPage: Page, ticketOrder: TicketOrder): Promise<{ clOrdId: string; status: string }> {
  await ticketPage.getByRole('group', { name: 'Lado da ordem' }).getByRole('button', { name: ticketOrder.side }).click();
  await ticketPage.getByLabel(/^Quantidade de/).fill(ticketOrder.quantity);
  await ticketPage.getByLabel('Preço por ação (R$)').fill(ticketOrder.price);
  const orderCreationResponse = ticketPage.waitForResponse((httpResponse) => httpResponse.request().method() === 'POST' && new URL(httpResponse.url()).pathname === CREATE_ORDER_ROUTE);
  const orderListRereadResponse = ticketPage.waitForResponse((httpResponse) => httpResponse.request().method() === 'GET' && new URL(httpResponse.url()).pathname === ORDERS_ROUTE);
  await ticketPage.getByRole('button', { name: /^Enviar ordem/ }).click();
  const createdOrderBody = ((await (await orderCreationResponse).json()) as { data: { clOrdId: string; status: string } }).data;
  // RF-07 and CA-41: after sending, the screen rereads only page 1.
  expect(new URL((await orderListRereadResponse).url()).search).toBe('?page=1');
  await expect(ticketPage.getByRole('button', { name: /^Enviar ordem/ })).toBeEnabled();
  return createdOrderBody;
}

function locateOrderListCard(ticketPage: Page) {
  return ticketPage.getByRole('region', { name: 'Compra/Venda' });
}

function locateOrderListRows(ticketPage: Page) {
  return locateOrderListCard(ticketPage).getByTestId('linha-da-ordem');
}

function locateOrderRowCell(orderRow: Locator, rowColumn: string) {
  return orderRow.locator(`td[data-column="${rowColumn}"]`);
}

function formatBrasiliaDayMonthYear(utcIsoInstant: string) {
  return new Intl.DateTimeFormat('pt-BR', { timeZone: 'America/Sao_Paulo', day: '2-digit', month: '2-digit', year: 'numeric' }).format(new Date(utcIsoInstant));
}

function formatBrasiliaHourMinute(utcIsoInstant: string) {
  return new Intl.DateTimeFormat('pt-BR', { timeZone: 'America/Sao_Paulo', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }).format(new Date(utcIsoInstant));
}

async function checkOrderRowAgainstServer(orderRow: Locator, orderOnServer: OrderStoredOnServer, expectedOrder: { asset: string; side: string; quantity: string; price: string; badge: 'Aceita' | 'Rejeitada'; reason: string }) {
  await expect(locateOrderRowCell(orderRow, 'date').locator('.order-row-day')).toHaveText(formatBrasiliaDayMonthYear(orderOnServer.receivedAt));
  await expect(locateOrderRowCell(orderRow, 'date').locator('.order-row-time')).toHaveText(formatBrasiliaHourMinute(orderOnServer.receivedAt));
  await expect(locateOrderRowCell(orderRow, 'date').locator('.order-row-time')).toHaveText(/^\d{2}:\d{2}$/);
  await expect(locateOrderRowCell(orderRow, 'date')).not.toContainText('agora');
  const rowBadge = locateOrderRowCell(orderRow, 'status').locator('.order-badge');
  await expect(rowBadge).toHaveText(expectedOrder.badge);
  const expectedBadgeColors = expectedOrder.badge === 'Aceita' ? ACCEPTED_BADGE_COLORS : REJECTED_BADGE_COLORS;
  await expect(rowBadge).toHaveCSS('background-color', expectedBadgeColors.background);
  await expect(rowBadge).toHaveCSS('color', expectedBadgeColors.text);
  await expect(locateOrderRowCell(orderRow, 'reject-reason')).toHaveText(expectedOrder.reason);
  await expect(locateOrderRowCell(orderRow, 'asset')).toHaveText(expectedOrder.asset);
  await expect(locateOrderRowCell(orderRow, 'side')).toHaveText(expectedOrder.side);
  await expect(locateOrderRowCell(orderRow, 'quantity')).toHaveText(expectedOrder.quantity);
  await expect(locateOrderRowCell(orderRow, 'price')).toHaveText(expectedOrder.price);
  await expect(locateOrderRowCell(orderRow, 'order-number')).toHaveText(orderOnServer.orderId);
  await expect(locateOrderRowCell(orderRow, 'send-identifier')).toHaveText(orderOnServer.clOrdId);
  await expect(locateOrderRowCell(orderRow, 'send-identifier')).toHaveText(/^[0-9a-f]{32}$/i);
}

test('CA-8: the left card is called "Compra/Venda" and the old texts do not appear', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByRole('heading', { level: 2, name: 'Compra/Venda' })).toBeVisible();
  await expect(page.getByText('Resposta da ordem')).toHaveCount(0);
  await expect(page.getByText('Minha Carteira')).toHaveCount(0);
});

test('CA-14: with no orders in the database, the card shows the empty state with icon and text, without a table', async ({ page }) => {
  await deleteAllOrdersOnServer(page);
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/');
  const emptyOrderList = locateOrderListCard(page).getByTestId('lista-de-ordens-vazia');
  await expect(emptyOrderList).toHaveText(EMPTY_LIST_TEXT);
  await expect(emptyOrderList.locator('svg')).toHaveCount(1);
  await expect(emptyOrderList.locator('svg')).toBeVisible();
  // It is the F3 DocumentIcon (Icons.tsx): a sheet with the folded corner and two text lines.
  await expect(emptyOrderList.locator('svg path')).toHaveCount(3);
  await expect(emptyOrderList.locator('svg path').nth(0)).toHaveAttribute('d', 'M14 3H7a2 2 0 00-2 2v14a2 2 0 002 2h10a2 2 0 002-2V8l-5-5z');
  await expect(emptyOrderList.locator('svg path').nth(1)).toHaveAttribute('d', 'M14 3v5h5');
  await expect(emptyOrderList.locator('svg path').nth(2)).toHaveAttribute('d', 'M9 13h6M9 17h6');
  await expect(locateOrderListCard(page).getByRole('table')).toHaveCount(0);
  await page.screenshot({ path: path.join(EVIDENCE_FOLDER, '06-empty-1440.png'), fullPage: true });
});

test('CA-11 and CA-36: two sent orders appear at the top, the newest first, with the 9 fields and the badges', async ({ page }) => {
  await deleteAllOrdersOnServer(page);
  try {
    await page.goto('/');
    await expect(locateOrderListCard(page).getByTestId('lista-de-ordens-vazia')).toBeVisible();
    // With exposure at zero, the first large buy fits the limit and the second does not: accepted, then rejected.
    const firstSentOrder = await sendOrderThroughTicket(page, { side: 'Compra', quantity: '99.999', price: '999,99' });
    const secondSentOrder = await sendOrderThroughTicket(page, { side: 'Compra', quantity: '99.999', price: '999,99' });
    expect(firstSentOrder.status).toBe('accepted');
    expect(secondSentOrder.status).toBe('rejected');

    const orderPageOnServer = await readFirstOrderPageOnServer(page);
    expect(orderPageOnServer.orders.map((orderOnServer) => orderOnServer.clOrdId)).toEqual([secondSentOrder.clOrdId, firstSentOrder.clOrdId]);
    await expect(locateOrderListCard(page).locator('thead th')).toHaveText(ORDER_LIST_COLUMNS);
    await expect(locateOrderListRows(page)).toHaveCount(2);
    const expectedPrice = brazilianRealFormatter.format(999.99);
    // CA-4: the rejected row carries the reason stored by the OrderAccumulator; the accepted one has none.
    expect(orderPageOnServer.orders.map((orderOnServer) => orderOnServer.rejectReason)).toEqual([LIMIT_REJECT_REASON, null]);
    await checkOrderRowAgainstServer(locateOrderListRows(page).nth(0), orderPageOnServer.orders[0], { asset: 'PETR4', side: 'Compra', quantity: '99.999', price: expectedPrice, badge: 'Rejeitada', reason: LIMIT_REJECT_REASON });
    await checkOrderRowAgainstServer(locateOrderListRows(page).nth(1), orderPageOnServer.orders[1], { asset: 'PETR4', side: 'Compra', quantity: '99.999', price: expectedPrice, badge: 'Aceita', reason: '—' });
  } finally {
    // The large buy leaves PETR4 close to the limit; clearing again keeps the following specs from seeing a rejection.
    await deleteAllOrdersOnServer(page);
  }
});

test('CA-12: the new order enters at the top without reloading and stays there after reloading', async ({ page }) => {
  await page.goto('/');
  const sentSellOrder = await sendOrderThroughTicket(page, { side: 'Venda', quantity: '7', price: '12,34' });
  const topOrderRow = locateOrderListRows(page).first();
  await expect(locateOrderRowCell(topOrderRow, 'send-identifier')).toHaveText(sentSellOrder.clOrdId);
  await expect(locateOrderRowCell(topOrderRow, 'side')).toHaveText('Venda');
  await expect(locateOrderRowCell(topOrderRow, 'quantity')).toHaveText('7');
  await expect(locateOrderRowCell(topOrderRow, 'price')).toHaveText(brazilianRealFormatter.format(12.34));
  await page.reload();
  await expect(locateOrderRowCell(locateOrderListRows(page).first(), 'send-identifier')).toHaveText(sentSellOrder.clOrdId);
});

test('CA-15: a fill-in error (400) shows in the red banner with the per-field errors, without a new row, and goes away on the next send', async ({ page }) => {
  await page.goto('/');
  await sendOrderThroughTicket(page, { side: 'Compra', quantity: '1', price: '10,00' });
  const totalOrdersBefore = (await readFirstOrderPageOnServer(page)).total;
  const topSendIdentifierBefore = await locateOrderRowCell(locateOrderListRows(page).first(), 'send-identifier').textContent();
  // The 400 only comes from an order the screen would already refuse; the response is the contract §1 body, "Invalid field".
  await page.route('**' + CREATE_ORDER_ROUTE, (orderCreationRoute) =>
    orderCreationRoute.fulfill({
      status: 400,
      contentType: 'application/problem+json',
      body: JSON.stringify({ type: 'urn:base-investimentos:problem:invalid-order', title: 'Dados inválidos', status: 400, detail: 'A ordem tem campos inválidos.', success: false, statusResultado: 'InvalidInput', errors: ['O preço deve ser múltiplo de 0,01.'] }),
    }),
  );
  await sendOrderThroughTicket(page, { side: 'Compra', quantity: '10', price: '10,00' });
  const sendFailureBanner = locateOrderListCard(page).getByTestId('faixa-da-falha-no-envio');
  await expect(sendFailureBanner).toBeVisible();
  await expect(sendFailureBanner.getByTestId('status-da-ordem')).toHaveText('Não enviada');
  await expect(sendFailureBanner.getByTestId('mensagem-da-ordem')).toHaveText('A ordem tem campos inválidos.');
  await expect(sendFailureBanner.getByTestId('erros-de-campo-da-ordem').getByRole('listitem')).toHaveText(['O preço deve ser múltiplo de 0,01.']);
  await expect(sendFailureBanner).toHaveCSS('background-color', REJECTED_BADGE_COLORS.background);
  await expect(sendFailureBanner.getByTestId('status-da-ordem')).toHaveCSS('color', REJECTED_BADGE_COLORS.text);
  // The banner sits at the top of the card, right below the title and before the table.
  const bannerTop = (await sendFailureBanner.boundingBox())!.y;
  const tableTop = (await locateOrderListCard(page).getByRole('table').boundingBox())!.y;
  expect(bannerTop).toBeLessThan(tableTop);
  expect((await readFirstOrderPageOnServer(page)).total).toBe(totalOrdersBefore);
  await expect(locateOrderRowCell(locateOrderListRows(page).first(), 'send-identifier')).toHaveText(topSendIdentifierBefore!);

  // RF-11: the next send (real, only held for 1.5 s) removes the banner right at the start, while it is still "Enviando…".
  await page.unroute('**' + CREATE_ORDER_ROUTE);
  await page.route('**' + CREATE_ORDER_ROUTE, async (heldOrderCreationRoute) => {
    await new Promise((releaseHeldRequest) => setTimeout(releaseHeldRequest, 1_500));
    await heldOrderCreationRoute.continue();
  });
  await page.getByLabel(/^Quantidade de/).fill('1');
  await page.getByLabel('Preço por ação (R$)').fill('10,00');
  await page.getByRole('button', { name: /^Enviar ordem/ }).click();
  await expect(locateOrderListCard(page).getByTestId('selo-enviando')).toBeVisible();
  await expect(sendFailureBanner).toHaveCount(0);
  await expect(locateOrderListCard(page).getByTestId('selo-enviando')).toBeVisible();
  await expect(locateOrderListCard(page).getByTestId('selo-enviando')).toHaveCount(0);
  await expect(sendFailureBanner).toHaveCount(0);
});

test('CA-15: with no communication with the server (503) the banner shows the message and the list gets no new row', async ({ page }) => {
  await page.goto('/');
  await sendOrderThroughTicket(page, { side: 'Compra', quantity: '1', price: '10,00' });
  const totalOrdersBefore = (await readFirstOrderPageOnServer(page)).total;
  await page.route('**' + CREATE_ORDER_ROUTE, (orderCreationRoute) =>
    orderCreationRoute.fulfill({ status: 503, contentType: 'application/problem+json', body: JSON.stringify({ type: 'urn:base-investimentos:problem:order-accumulator-unavailable', title: 'Serviço indisponível', status: 503, detail: 'Não foi possível falar com o OrderAccumulator. Tente de novo em instantes.', success: false, statusResultado: 'ServiceUnavailable', errors: [] }) }),
  );
  await sendOrderThroughTicket(page, { side: 'Compra', quantity: '10', price: '10,00' });
  const sendFailureBanner = locateOrderListCard(page).getByTestId('faixa-da-falha-no-envio');
  await expect(sendFailureBanner.getByTestId('status-da-ordem')).toHaveText('Erro de comunicação');
  await expect(sendFailureBanner.getByTestId('mensagem-da-ordem')).toHaveText('A ordem não foi confirmada: o servidor de ordens não respondeu. Tente de novo em instantes.');
  await expect(sendFailureBanner.getByTestId('erros-de-campo-da-ordem')).toHaveCount(0);
  expect((await readFirstOrderPageOnServer(page)).total).toBe(totalOrdersBefore);
});

test('CA-16: while the order travels, the card shows the amber "Enviando…" badge and the button is disabled', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/');
  await page.route('**' + CREATE_ORDER_ROUTE, async (heldOrderCreationRoute) => {
    await new Promise((releaseHeldRequest) => setTimeout(releaseHeldRequest, 1_500));
    await heldOrderCreationRoute.continue();
  });
  await page.getByLabel(/^Quantidade de/).fill('10');
  await page.getByLabel('Preço por ação (R$)').fill('10,00');
  await page.getByRole('button', { name: 'Enviar ordem de compra' }).click();
  const sendingBadge = locateOrderListCard(page).getByTestId('selo-enviando');
  await expect(sendingBadge).toHaveText('Enviando…');
  await expect(sendingBadge).toHaveCSS('background-color', SENDING_BADGE_COLORS.background);
  await expect(sendingBadge).toHaveCSS('color', SENDING_BADGE_COLORS.text);
  await expect(page.getByRole('button', { name: 'Enviando…' })).toBeDisabled();
  await page.screenshot({ path: path.join(EVIDENCE_FOLDER, '06-sending-1440.png') });
  await expect(sendingBadge).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Enviar ordem de compra' })).toBeEnabled();
});

test('CA-41 and CA-34: on opening, the list is read once with page=1; idle for 30 s, no new read', async ({ context }) => {
  test.setTimeout(60_000);
  const countedPage = await context.newPage();
  const orderListReads: string[] = [];
  const exposureReads: string[] = [];
  countedPage.on('request', (pageRequest) => {
    const requestPath = new URL(pageRequest.url()).pathname;
    if (requestPath === ORDERS_ROUTE && pageRequest.method() === 'GET') orderListReads.push(pageRequest.url());
    if (requestPath === EXPOSURES_ROUTE) exposureReads.push(pageRequest.url());
  });
  await countedPage.goto('/');
  await expect(locateOrderListCard(countedPage).getByRole('table').or(locateOrderListCard(countedPage).getByTestId('lista-de-ordens-vazia'))).toBeVisible();
  expect(orderListReads).toHaveLength(1);
  expect(new URL(orderListReads[0]).search).toBe('?page=1');
  const orderListReadsOnOpening = orderListReads.length;
  const exposureReadsOnOpening = exposureReads.length;
  await countedPage.waitForTimeout(30_000);
  expect(orderListReads).toHaveLength(orderListReadsOnOpening);
  expect(exposureReads).toHaveLength(exposureReadsOnOpening);
});

test('CA-11 and RNF-01: at 1440 px the 32-letter codes appear whole, each order on one row, in the mockup colors', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/');
  await sendOrderThroughTicket(page, { side: 'Compra', quantity: '1', price: '10,00' });
  const topOrderRow = locateOrderListRows(page).first();
  for (const codeColumn of ['order-number', 'send-identifier']) {
    const codeCell = locateOrderRowCell(topOrderRow, codeColumn);
    await expect(codeCell).toHaveCSS('white-space', 'nowrap');
    const codeFitsWhole = await codeCell.evaluate((cellOnPage) => cellOnPage.scrollWidth <= cellOnPage.clientWidth);
    expect(codeFitsWhole, codeColumn).toBe(true);
  }
  const orderTableFrame = locateOrderListCard(page).locator('.order-table-frame');
  const tableFitsWithoutScrolling = await orderTableFrame.evaluate((frameOnPage) => frameOnPage.scrollWidth <= frameOnPage.clientWidth);
  expect(tableFitsWithoutScrolling).toBe(true);
  await expect(orderTableFrame).toHaveCSS('background-color', 'rgb(11, 23, 25)');
  const dateHeader = locateOrderListCard(page).locator('thead th').first();
  // At 1440 px the mockup table applies: header in view and the order on a single row, without a per-field label.
  await expect(dateHeader).toBeVisible();
  await expect(topOrderRow).toHaveCSS('display', 'table-row');
  await expect(dateHeader).toHaveCSS('font-size', '11px');
  await expect(dateHeader).toHaveCSS('text-transform', 'uppercase');
  await expect(dateHeader).toHaveCSS('color', 'rgb(143, 163, 161)');
  const assetCell = locateOrderRowCell(topOrderRow, 'asset');
  await expect(assetCell).toHaveCSS('background-color', 'rgb(14, 27, 30)');
  await topOrderRow.hover();
  await expect(assetCell).toHaveCSS('background-color', 'rgb(18, 36, 39)');
  const topOrderBadge = locateOrderRowCell(topOrderRow, 'status').locator('.order-badge');
  await expect(topOrderBadge).toHaveCSS('font-size', '12px');
  await expect(topOrderBadge).toHaveCSS('font-weight', '800');
  await expect(topOrderBadge).toHaveCSS('padding', '6px 11px');
  await expect(topOrderBadge).toHaveCSS('border-radius', '999px');
  await page.mouse.move(0, 0);
  await page.screenshot({ path: path.join(EVIDENCE_FOLDER, '06-list-1440.png'), fullPage: true });
});

const ORDER_FIELD_LABELS = [
  ['date', 'Data'], ['status', 'Status'], ['reject-reason', 'Motivo'], ['asset', 'Ativo'], ['side', 'Lado'], ['quantity', 'Quantidade'],
  ['price', 'Preço'], ['order-number', 'Número da ordem'], ['send-identifier', 'Identificador do envio'],
] as const;
const ORDERS_PER_PAGE = 10;

// Page 1 full with the widest values the ticket accepts (99,999 at R$ 999.99) and one "Rejeitada"
// (widest badge). Nine PETR4 orders alternate buy and sell, so PETR4 ends close to the limit and
// the second large VIIA4 buy exceeds the limit. The afterEach clears everything for the following specs.
async function storeFullListOfWideOrders(ticketPage: Page) {
  await deleteAllOrdersOnServer(ticketPage);
  const seedOrders = [
    ...Array.from({ length: 9 }, (_, orderPosition) => ({ symbol: 'PETR4', side: orderPosition % 2 === 0 ? 'buy' : 'sell' })),
    { symbol: 'VIIA4', side: 'buy' },
    { symbol: 'VIIA4', side: 'buy' },
  ];
  const storedOrderStatuses: string[] = [];
  for (const seedOrder of seedOrders) {
    const storedOrderResponse = await ticketPage.request.post(CREATE_ORDER_ROUTE, { data: { ...seedOrder, quantity: 99_999, price: 999.99 } });
    expect(storedOrderResponse.status()).toBe(200);
    storedOrderStatuses.push(((await storedOrderResponse.json()) as { data: { status: string } }).data.status);
  }
  expect(storedOrderStatuses.at(-1)).toBe('rejected');
}

async function countCellTextLines(orderCell: Locator) {
  return orderCell.evaluate((cellOnPage) => {
    const cellTextRange = document.createRange();
    cellTextRange.selectNodeContents(cellOnPage);
    return new Set([...cellTextRange.getClientRects()].map((lineBox) => Math.round(lineBox.top))).size;
  });
}

async function checkNothingScrollsSideways(ticketPage: Page) {
  const pageFitsWithoutSidewaysScroll = await ticketPage.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth);
  expect(pageFitsWithoutSidewaysScroll).toBe(true);
  const frameFitsWithoutScrolling = await locateOrderListCard(ticketPage).locator('.order-table-frame').evaluate((frameOnPage) => frameOnPage.scrollWidth <= frameOnPage.clientWidth);
  expect(frameFitsWithoutScrolling).toBe(true);
}

test.describe('full list of wide orders', () => {
  test.beforeEach(async ({ page }) => {
    await storeFullListOfWideOrders(page);
  });

  test.afterEach(async ({ page }) => {
    await deleteAllOrdersOnServer(page);
  });

  // ASSUMI-04, card of 1030 px or more: the whole table, each code on a single line (RF-03, CA-11).
  for (const windowWidth of [1440, 1920]) {
    test(`CA-11 and RF-03: at ${windowWidth} px, with the worst case, each order is a table row and the 32-letter codes stay whole on one line`, async ({ page }) => {
      await page.setViewportSize({ width: windowWidth, height: 900 });
      await page.goto('/');
      await expect(locateOrderListRows(page)).toHaveCount(ORDERS_PER_PAGE);
      await expect(locateOrderRowCell(locateOrderListRows(page).first(), 'status').locator('.order-badge')).toHaveText('Rejeitada');
      await expect(locateOrderListCard(page).locator('thead')).toBeVisible();
      await checkNothingScrollsSideways(page);
      for (let rowPosition = 0; rowPosition < ORDERS_PER_PAGE; rowPosition++) {
        const orderRow = locateOrderListRows(page).nth(rowPosition);
        await expect(orderRow, `row ${rowPosition + 1}`).toHaveCSS('display', 'table-row');
        for (const codeColumn of ['order-number', 'send-identifier']) {
          const codeCell = locateOrderRowCell(orderRow, codeColumn);
          await expect(codeCell, `row ${rowPosition + 1} / ${codeColumn}`).toHaveText(/^[0-9a-f]{32}$/i);
          expect(await countCellTextLines(codeCell), `row ${rowPosition + 1} / ${codeColumn}`).toBe(1);
          const codeFitsWhole = await codeCell.evaluate((cellOnPage) => cellOnPage.scrollWidth <= cellOnPage.clientWidth);
          expect(codeFitsWhole, `row ${rowPosition + 1} / ${codeColumn}`).toBe(true);
        }
      }
      if (windowWidth === 1440) await page.screenshot({ path: path.join(EVIDENCE_FOLDER, '06-full-list-1440.png'), fullPage: true });
    });
  }

  // ASSUMI-04, card from 800 to 1029 px: still one order per table row; only the codes wrap, at most onto 2 lines.
  for (const windowWidth of [860, 1280]) {
    test(`ASSUMI-04: at ${windowWidth} px, with the worst case, the table keeps one order per row and each code takes at most 2 lines`, async ({ page }) => {
      await page.setViewportSize({ width: windowWidth, height: 900 });
      await page.goto('/');
      await expect(locateOrderListRows(page)).toHaveCount(ORDERS_PER_PAGE);
      await expect(locateOrderListCard(page).locator('thead')).toBeVisible();
      await expect(locateOrderListCard(page).locator('thead th')).toHaveText(ORDER_LIST_COLUMNS);
      await checkNothingScrollsSideways(page);
      for (let rowPosition = 0; rowPosition < ORDERS_PER_PAGE; rowPosition++) {
        const orderRow = locateOrderListRows(page).nth(rowPosition);
        await expect(orderRow, `row ${rowPosition + 1}`).toHaveCSS('display', 'table-row');
        for (const codeColumn of ['order-number', 'send-identifier']) {
          const codeCell = locateOrderRowCell(orderRow, codeColumn);
          await expect(codeCell, `row ${rowPosition + 1} / ${codeColumn}`).toHaveText(/^[0-9a-f]{32}$/i);
          expect(await countCellTextLines(codeCell), `row ${rowPosition + 1} / ${codeColumn}`).toBeLessThanOrEqual(2);
        }
      }
      if (windowWidth === 1280) await page.screenshot({ path: path.join(EVIDENCE_FOLDER, '06-full-list-1280.png'), fullPage: true });
    });
  }

  // ASSUMI-04, card narrower than 800 px: grid block, label above each value, nothing outside the card (CA-26).
  for (const { windowWidth, blockColumns, maxBlockHeightOnScreen } of [
    // In 2 columns the reason (9th field) takes a whole row of its own: 40 px more than the 8-field block.
    { windowWidth: 375, blockColumns: 2, maxBlockHeightOnScreen: 320 },
    { windowWidth: 861, blockColumns: 2, maxBlockHeightOnScreen: 320 },
    { windowWidth: 1180, blockColumns: 4, maxBlockHeightOnScreen: 180 },
  ]) {
    test(`CA-26 and ASSUMI-04: at ${windowWidth} px each order becomes a ${blockColumns}-column block with the 9 labeled fields, label above the value, all inside the card`, async ({ page }) => {
      await page.setViewportSize({ width: windowWidth, height: 900 });
      await page.goto('/');
      await expect(locateOrderListRows(page)).toHaveCount(ORDERS_PER_PAGE);
      // In the block layout the table header leaves the scene: each field carries its own label.
      await expect(locateOrderListCard(page).locator('thead')).toBeHidden();
      await checkNothingScrollsSideways(page);
      const cardBox = (await locateOrderListCard(page).boundingBox())!;
      for (let rowPosition = 0; rowPosition < ORDERS_PER_PAGE; rowPosition++) {
        const orderRow = locateOrderListRows(page).nth(rowPosition);
        await expect(orderRow, `row ${rowPosition + 1}`).toHaveCSS('display', 'grid');
        const blockGridColumnCount = await orderRow.evaluate((rowOnPage) => getComputedStyle(rowOnPage).gridTemplateColumns.split(' ').length);
        expect(blockGridColumnCount, `row ${rowPosition + 1}`).toBe(blockColumns);
        const orderBox = (await orderRow.boundingBox())!;
        expect(orderBox.height, `row ${rowPosition + 1}: block height`).toBeLessThanOrEqual(maxBlockHeightOnScreen);
        for (const [fieldColumn, fieldLabel] of ORDER_FIELD_LABELS) {
          const fieldCell = locateOrderRowCell(orderRow, fieldColumn);
          const fieldName = `row ${rowPosition + 1} / ${fieldColumn}`;
          await expect(fieldCell, fieldName).toBeVisible();
          expect(await fieldCell.evaluate((cellOnPage) => getComputedStyle(cellOnPage, '::before').content), fieldName).toBe(`"${fieldLabel}"`);
          // Label above the value: the cell stacks in a column, without pushing label and value to the ends.
          await expect(fieldCell, fieldName).toHaveCSS('flex-direction', 'column');
          const fieldBox = (await fieldCell.boundingBox())!;
          expect(fieldBox.x, `${fieldName}: starts inside the card`).toBeGreaterThanOrEqual(cardBox.x);
          expect(fieldBox.x + fieldBox.width, `${fieldName}: ends inside the card`).toBeLessThanOrEqual(cardBox.x + cardBox.width);
        }
        // The reason is free text: it takes two grid columns (the whole row in 2 columns), never a single one.
        const reasonBox = (await locateOrderRowCell(orderRow, 'reject-reason').boundingBox())!;
        const dateBox = (await locateOrderRowCell(orderRow, 'date').boundingBox())!;
        const statusBox = (await locateOrderRowCell(orderRow, 'status').boundingBox())!;
        expect(reasonBox.width, `row ${rowPosition + 1}: reason spans two columns`).toBeGreaterThanOrEqual(statusBox.x + statusBox.width - dateBox.x - 1);
      }
      if (windowWidth === 375) await page.screenshot({ path: path.join(EVIDENCE_FOLDER, '06-list-375.png'), fullPage: true });
    });
  }
});

test('ASSUMI-01: if the list cannot be read, the card shows the error in place of the table, without the empty state', async ({ page }) => {
  // Only the list read fails; the body is the contract 503. The ticket and the exposure stay real.
  await page.route('**' + ORDERS_ROUTE + '?page=1', (orderListReadRoute) =>
    orderListReadRoute.fulfill({ status: 503, contentType: 'application/problem+json', body: JSON.stringify({ type: 'urn:base-investimentos:problem:order-accumulator-unavailable', title: 'Serviço indisponível', status: 503, detail: 'Não foi possível falar com o OrderAccumulator. Tente de novo em instantes.', success: false, statusResultado: 'ServiceUnavailable', errors: [] }) }),
  );
  await page.goto('/');
  const errorNotice = locateOrderListCard(page).getByRole('alert');
  await expect(errorNotice).toHaveText('Não foi possível ler as ordens agora. Tente de novo em instantes.');
  await expect(errorNotice).toHaveCSS('color', REJECTED_BADGE_COLORS.text);
  await expect(locateOrderListCard(page).getByRole('table')).toHaveCount(0);
  await expect(locateOrderListCard(page).getByTestId('lista-de-ordens-vazia')).toHaveCount(0);
});

test('ASSUMI-07: while the first read has not come back, the card says it is loading the orders', async ({ page }) => {
  let releaseOrderListRead: () => void = () => {};
  const orderListReadReleased = new Promise<void>((resolveOrderListRead) => { releaseOrderListRead = resolveOrderListRead; });
  await page.route('**' + ORDERS_ROUTE + '?page=1', async (orderListReadRoute) => {
    await orderListReadReleased;
    await orderListReadRoute.continue();
  });
  await page.goto('/');
  await expect(locateOrderListCard(page).getByText('Carregando as ordens…', { exact: true })).toBeVisible();
  await expect(locateOrderListCard(page)).toHaveAttribute('aria-busy', 'true');
  releaseOrderListRead();
  await expect(locateOrderListCard(page).getByText('Carregando as ordens…', { exact: true })).toHaveCount(0);
  await expect(locateOrderListCard(page)).toHaveAttribute('aria-busy', 'false');
});

test('ASSUMI-03: an order stored without symbol and without side (rejection coming straight through FIX) shows "—" in both fields', async ({ page }) => {
  // Through the screen and the API a null side cannot be stored; the case only comes from FIX, so the read returns the
  // contract body with null symbol and side (format checked by F1 on the candidate API).
  await page.route('**' + ORDERS_ROUTE + '?page=1', (orderListReadRoute) =>
    orderListReadRoute.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ success: true, status: 'Ok', message: 'Página de ordens lida.', data: { page: 1, pageSize: 10, total: 1, orders: [{ receivedAt: '2026-10-04T23:30:00.123456Z', status: 'rejected', symbol: null, side: null, quantity: 5, price: 1.1, orderId: 'a'.repeat(32), clOrdId: 'b'.repeat(32) }] }, errors: [], errorCode: null }),
    }),
  );
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/');
  const rowWithoutSymbolOrSide = locateOrderListRows(page).first();
  await expect(locateOrderRowCell(rowWithoutSymbolOrSide, 'asset')).toHaveText('—');
  await expect(locateOrderRowCell(rowWithoutSymbolOrSide, 'side')).toHaveText('—');
  await expect(locateOrderRowCell(rowWithoutSymbolOrSide, 'status').locator('.order-badge')).toHaveText('Rejeitada');
  await expect(locateOrderRowCell(rowWithoutSymbolOrSide, 'date').locator('.order-row-day')).toHaveText('04/10/2026');
  await expect(locateOrderRowCell(rowWithoutSymbolOrSide, 'date').locator('.order-row-time')).toHaveText('20:30');
});

// CA-4: the three kinds of row side by side, all stored by the real OrderAccumulator: accepted, rejected by the limit
// and rejected by a field (price 10.005 goes as text, as the test mode sends it, and the OrderAccumulator refuses it).
test('CA-4 and CA-10: GET /api/orders brings the reason of each order, and the "Motivo" column shows it right after "Status"', async ({ page }) => {
  await deleteAllOrdersOnServer(page);
  try {
    const seedOrders = [
      { symbol: 'PETR4', side: 'buy', quantity: 99_999, price: 999.99 },
      { symbol: 'PETR4', side: 'buy', quantity: 99_999, price: 999.99 },
      { symbol: 'PETR4', side: 'buy', quantity: '100', price: '10.005' },
    ];
    for (const seedOrder of seedOrders) expect((await page.request.post(CREATE_ORDER_ROUTE, { data: seedOrder })).status()).toBe(200);
    const orderListResponse = await page.request.get(ORDERS_ROUTE + '?page=1');
    expect(orderListResponse.status()).toBe(200);
    const orderListBody = await orderListResponse.text();
    fs.mkdirSync(EVIDENCE_FOLDER, { recursive: true });
    fs.writeFileSync(path.join(EVIDENCE_FOLDER, '07-get-api-orders-page-1.json'), orderListBody);
    const ordersOnServer = (JSON.parse(orderListBody) as { data: OrderPageOnServer }).data.orders;
    expect(ordersOnServer.map((orderOnServer) => [orderOnServer.status, orderOnServer.rejectReason])).toEqual([
      ['rejected', PRICE_FIELD_REJECT_REASON],
      ['rejected', LIMIT_REJECT_REASON],
      ['accepted', null],
    ]);

    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto('/');
    await expect(locateOrderListCard(page).locator('thead th')).toHaveText(ORDER_LIST_COLUMNS);
    await expect(locateOrderListRows(page)).toHaveCount(3);
    const [fieldRejectedRow, limitRejectedRow, acceptedRow] = [0, 1, 2].map((rowPosition) => locateOrderListRows(page).nth(rowPosition));
    await expect(locateOrderRowCell(fieldRejectedRow, 'reject-reason')).toHaveText(PRICE_FIELD_REJECT_REASON);
    await expect(locateOrderRowCell(fieldRejectedRow, 'price')).toHaveText('R$ 10,005');
    await expect(locateOrderRowCell(limitRejectedRow, 'reject-reason')).toHaveText(LIMIT_REJECT_REASON);
    await expect(locateOrderRowCell(acceptedRow, 'reject-reason')).toHaveText('—');
    // Mockup-01: the reason is smaller than the row and wraps; the dash of the accepted row is dimmed.
    const limitReasonCell = locateOrderRowCell(limitRejectedRow, 'reject-reason');
    await expect(limitReasonCell).toHaveCSS('font-size', '12px');
    await expect(limitReasonCell).toHaveCSS('white-space', 'normal');
    await expect(limitReasonCell).toHaveCSS('color', 'rgb(201, 215, 213)');
    expect(await countCellTextLines(limitReasonCell)).toBeGreaterThan(1);
    await expect(locateOrderRowCell(acceptedRow, 'reject-reason').locator('.order-row-no-reason')).toHaveCSS('color', 'rgb(143, 163, 161)');
    await checkNothingScrollsSideways(page);
    await page.mouse.move(0, 0);
    await locateOrderListCard(page).screenshot({ path: path.join(EVIDENCE_FOLDER, '07-coluna-motivo-1440.png') });
  } finally {
    await deleteAllOrdersOnServer(page);
  }
});
