import { execFileSync } from 'node:child_process';
import { expect, test, type Page } from '@playwright/test';
import { CREATE_ORDER_ROUTE, EXPOSURES_ROUTE, ORDERS_ROUTE } from '../src/services/ordersService';

// CA-11 and CA-27 against the real stack: the OrderGenerator waits only Fix__ExecutionReportTimeoutSeconds (2 s in the
// scenario override) and the database is paused for E2E_DB_PAUSE_MS at the click, so the POST answers 503 and the
// order enters later. It needs the Postgres container of the stack under test, so it has its own config
// (playwright.slow-answer.config.ts) and stays out of the default run.

function readPostgresContainerName() {
  const postgresContainerName = process.env.E2E_POSTGRES_CONTAINER;
  if (!postgresContainerName) throw new Error('E2E_POSTGRES_CONTAINER is required: the scenario pauses the database of the stack under test');
  return postgresContainerName;
}

const POSTGRES_CONTAINER = readPostgresContainerName();
const DATABASE_PAUSE_IN_MS = Number(process.env.E2E_DB_PAUSE_MS ?? 3_000);
const SCREENSHOT_FOLDER = process.env.E2E_PROVAS_DIR;
const MAYBE_ACCEPTED_MESSAGE = 'A ordem pode ter sido aceita. Confira a lista antes de enviar de novo.';
const brazilianReaisFormatter = new Intl.NumberFormat('pt-BR', { style: 'currency', currency: 'BRL' });
// A read of the list that fails answers like the server does when it cannot read the orders (as in order-list.spec.ts).
const ORDER_LIST_READ_FAILURE = {
  status: 503,
  contentType: 'application/problem+json',
  body: JSON.stringify({ type: 'urn:base-investimentos:problem:order-accumulator-unavailable', title: 'Serviço indisponível', status: 503, detail: 'Não foi possível ler as ordens agora.', success: false, statusResultado: 'ServiceUnavailable', errors: [] }),
};
const WARNING_BOX_COLORS = { border: 'color(srgb 0.956863 0.772549 0.415686 / 0.38)', text: 'rgb(244, 197, 106)', background: 'rgba(242, 184, 75, 0.14)' };

function isDatabasePaused() {
  return execFileSync('docker', ['inspect', '-f', '{{.State.Paused}}', POSTGRES_CONTAINER]).toString().trim() === 'true';
}

// A failed assertion must never leave the database of the stack paused.
test.afterEach(() => {
  if (isDatabasePaused()) execFileSync('docker', ['unpause', POSTGRES_CONTAINER]);
});

type ScreenRead = { route: string; startedAtInMs: number };

function watchScreenReads(orderTicketPage: Page) {
  const screenReads: ScreenRead[] = [];
  orderTicketPage.on('request', (pageRequest) => {
    const requestRoute = new URL(pageRequest.url()).pathname;
    if (pageRequest.method() === 'GET' && (requestRoute === ORDERS_ROUTE || requestRoute === EXPOSURES_ROUTE)) {
      screenReads.push({ route: requestRoute, startedAtInMs: Date.now() });
    }
  });
  return screenReads;
}

test('CA-11 and CA-27: a late answer (503) shows the warning, rereads list and exposure by itself at most 3 times, 2 s apart, and the order shows up', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  // Deleting before the page opens: a reload would cut the opening reads still in flight.
  expect((await page.request.delete(ORDERS_ROUTE)).status()).toBe(204);
  await page.goto('/');
  await expect(page.getByTestId('lista-de-ordens-vazia')).toBeVisible();

  await page.getByRole('group', { name: 'Símbolo' }).getByRole('button', { name: 'PETR4', exact: true }).click();
  await page.getByRole('group', { name: 'Lado da ordem' }).getByRole('button', { name: 'Compra' }).click();
  await page.getByLabel(/^Quantidade de/).fill('100');
  await page.getByLabel('Preço por ação (R$)').fill('10,00');
  const screenReads = watchScreenReads(page);
  const createOrderResponse = page.waitForResponse(
    (httpResponse) => httpResponse.request().method() === 'POST' && new URL(httpResponse.url()).pathname === CREATE_ORDER_ROUTE,
  );
  // The pause starts right before the click, so the order is surely held by the database and not decided before it.
  execFileSync('docker', ['pause', POSTGRES_CONTAINER]);
  await page.getByRole('button', { name: /^Enviar ordem/ }).click();
  const databaseResume = new Promise<void>((resumeDone) =>
    setTimeout(() => {
      execFileSync('docker', ['unpause', POSTGRES_CONTAINER]);
      resumeDone();
    }, DATABASE_PAUSE_IN_MS),
  );

  const lateAnswer = await createOrderResponse;
  const warningShownAtInMs = Date.now();
  expect(lateAnswer.status()).toBe(503);
  const lateAnswerProblem = (await lateAnswer.json()) as { type: string; detail: string };
  expect(lateAnswerProblem.type).toBe('urn:base-investimentos:problem:execution-report-timeout');

  const warningBox = page.getByRole('region', { name: 'Compra/Venda' }).getByTestId('faixa-da-falha-no-envio');
  await expect(warningBox.getByTestId('status-da-ordem')).toHaveText('Sem confirmação');
  await expect(warningBox.getByTestId('mensagem-da-ordem')).toHaveText(MAYBE_ACCEPTED_MESSAGE);
  await expect(warningBox).not.toContainText('Tente de novo');
  await expect(warningBox.getByTestId('status-da-ordem')).toHaveCSS('color', WARNING_BOX_COLORS.text);
  for (const boxSide of ['top', 'right', 'bottom', 'left']) {
    await expect(warningBox).toHaveCSS(`border-${boxSide}-color`, WARNING_BOX_COLORS.border);
  }
  await expect(warningBox).toHaveCSS('background-color', WARNING_BOX_COLORS.background);
  await expect(warningBox.getByTestId('ordem-da-resposta')).toHaveText('PETR4 · Compra · 100 × R$ 10,00');
  if (SCREENSHOT_FOLDER) await page.screenshot({ path: `${SCREENSHOT_FOLDER}/06-demora-antes-1440.png` });
  await databaseResume;

  // The order that entered shows up with no click: top row of the list and the PETR4 exposure.
  const topOrderRow = page.getByTestId('linha-da-ordem');
  await expect(topOrderRow).toHaveCount(1, { timeout: 10_000 });
  await expect(topOrderRow.locator('td[data-column="asset"]')).toHaveText('PETR4');
  await expect(topOrderRow.locator('td[data-column="status"] .order-badge')).toHaveText('Aceita');
  await expect(topOrderRow.locator('td[data-column="price"]')).toHaveText(brazilianReaisFormatter.format(10));
  await expect(page.getByTestId('exposicao-PETR4').getByTestId('exposicao-atual')).toHaveText(brazilianReaisFormatter.format(1_000));
  await expect(warningBox.getByTestId('mensagem-da-ordem')).toHaveText(MAYBE_ACCEPTED_MESSAGE);
  if (SCREENSHOT_FOLDER) await page.screenshot({ path: `${SCREENSHOT_FOLDER}/06-demora-depois-1440.png` });

  // Zero GET with the screen idle after the cycle: 10 s later the count has not moved.
  const orderListReadsWhenOrderShowed = screenReads.filter((screenRead) => screenRead.route === ORDERS_ROUTE).length;
  await page.waitForTimeout(10_000);
  const orderListReads = screenReads.filter((screenRead) => screenRead.route === ORDERS_ROUTE);
  const exposureReads = screenReads.filter((screenRead) => screenRead.route === EXPOSURES_ROUTE);
  expect(orderListReads).toHaveLength(orderListReadsWhenOrderShowed);
  expect(orderListReads.length).toBeGreaterThanOrEqual(1);
  expect(orderListReads.length).toBeLessThanOrEqual(3);
  expect(exposureReads).toHaveLength(orderListReads.length);
  for (let readIndex = 1; readIndex < orderListReads.length; readIndex++) {
    expect(orderListReads[readIndex].startedAtInMs - orderListReads[readIndex - 1].startedAtInMs).toBeGreaterThanOrEqual(1_900);
  }
  expect(orderListReads[0].startedAtInMs).toBeGreaterThanOrEqual(warningShownAtInMs - 1_000);
  test.info().annotations.push({
    type: 'reads after the warning',
    description: orderListReads.map((screenRead) => `${screenRead.startedAtInMs - warningShownAtInMs} ms`).join(', '),
  });
  // The 503 no longer names the internal service (CA-11) and says the same as the screen (G-1).
  expect(lateAnswerProblem.detail).toBe(MAYBE_ACCEPTED_MESSAGE);
});

async function startFromEmptyBoard(orderTicketPage: Page, viewportWidth: number) {
  await orderTicketPage.setViewportSize({ width: viewportWidth, height: 900 });
  expect((await orderTicketPage.request.delete(ORDERS_ROUTE)).status()).toBe(204);
  await orderTicketPage.goto('/');
  await expect(orderTicketPage.getByTestId('lista-de-ordens-vazia')).toBeVisible();
}

async function fillOrderTicket(orderTicketPage: Page, orderSymbol: string, quantity: string, price: string) {
  await orderTicketPage.getByRole('group', { name: 'Símbolo' }).getByRole('button', { name: orderSymbol, exact: true }).click();
  await orderTicketPage.getByRole('group', { name: 'Lado da ordem' }).getByRole('button', { name: 'Compra' }).click();
  await orderTicketPage.getByLabel(/^Quantidade de/).fill(quantity);
  await orderTicketPage.getByLabel('Preço por ação (R$)').fill(price);
}

// Clicks "Enviar ordem" with the database paused for DATABASE_PAUSE_IN_MS, and returns the late 503 and the resume.
async function sendWithDatabasePaused(orderTicketPage: Page) {
  const createOrderResponse = orderTicketPage.waitForResponse(
    (httpResponse) => httpResponse.request().method() === 'POST' && new URL(httpResponse.url()).pathname === CREATE_ORDER_ROUTE,
  );
  execFileSync('docker', ['pause', POSTGRES_CONTAINER]);
  await orderTicketPage.getByRole('button', { name: /^Enviar ordem/ }).click();
  const databaseResume = new Promise<void>((resumeDone) =>
    setTimeout(() => {
      execFileSync('docker', ['unpause', POSTGRES_CONTAINER]);
      resumeDone();
    }, DATABASE_PAUSE_IN_MS),
  );
  const lateAnswer = await createOrderResponse;
  expect(lateAnswer.status()).toBe(503);
  return { lateAnswerAtInMs: Date.now(), databaseResume };
}

test('RF-12 at 375px: the read right after the warning fails, it counts as a round, and the cycle rereads 2 s later until the order shows up', async ({ page }) => {
  await startFromEmptyBoard(page, 375);
  await fillOrderTicket(page, 'PETR4', '100', '10,00');
  const screenReads = watchScreenReads(page);
  // Only the first list read after the send fails (503); every other read reaches the real server.
  let shouldFailNextOrderListRead = true;
  await page.route(`**${ORDERS_ROUTE}?page=*`, async (orderListReadRoute) => {
    if (!shouldFailNextOrderListRead) return orderListReadRoute.continue();
    shouldFailNextOrderListRead = false;
    return orderListReadRoute.fulfill(ORDER_LIST_READ_FAILURE);
  });
  const { lateAnswerAtInMs, databaseResume } = await sendWithDatabasePaused(page);

  const warningBox = page.getByRole('region', { name: 'Compra/Venda' }).getByTestId('faixa-da-falha-no-envio');
  await expect(warningBox).toBeVisible();
  await expect(warningBox.getByTestId('status-da-ordem')).toHaveText('Sem confirmação');
  await expect(warningBox.getByTestId('mensagem-da-ordem')).toHaveText(MAYBE_ACCEPTED_MESSAGE);
  await expect(warningBox).toHaveCSS('background-color', WARNING_BOX_COLORS.background);
  expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBe(0);
  if (SCREENSHOT_FOLDER) await page.screenshot({ path: `${SCREENSHOT_FOLDER}/06-demora-antes-375.png`, fullPage: true });
  await databaseResume;

  await expect(page.getByTestId('linha-da-ordem')).toHaveCount(1, { timeout: 10_000 });
  await expect(page.getByTestId('linha-da-ordem').locator('td[data-column="asset"]')).toHaveText('PETR4');
  if (SCREENSHOT_FOLDER) await page.screenshot({ path: `${SCREENSHOT_FOLDER}/06-demora-depois-375.png`, fullPage: true });
  await page.waitForTimeout(8_000);
  const orderListReads = screenReads.filter((screenRead) => screenRead.route === ORDERS_ROUTE);
  // The failed read plus at least one read of the cycle; never more than 3, never closer than 2 s, none after the order showed up.
  expect(orderListReads.length).toBeGreaterThanOrEqual(2);
  expect(orderListReads.length).toBeLessThanOrEqual(3);
  expect(orderListReads[0].startedAtInMs).toBeGreaterThanOrEqual(lateAnswerAtInMs - 1_000);
  for (let readIndex = 1; readIndex < orderListReads.length; readIndex++) {
    expect(orderListReads[readIndex].startedAtInMs - orderListReads[readIndex - 1].startedAtInMs).toBeGreaterThanOrEqual(1_900);
  }
});

test('RF-12: a new send cancels the rereads of the previous unconfirmed send', async ({ page }) => {
  await startFromEmptyBoard(page, 1440);
  await fillOrderTicket(page, 'PETR4', '100', '10,00');
  // While the first order is unconfirmed its list reads fail, so its cycle would keep rereading for 4 s.
  let shouldFailOrderListReads = true;
  await page.route(`**${ORDERS_ROUTE}?page=*`, (orderListReadRoute) =>
    shouldFailOrderListReads ? orderListReadRoute.fulfill(ORDER_LIST_READ_FAILURE) : orderListReadRoute.continue(),
  );
  const { databaseResume } = await sendWithDatabasePaused(page);
  await expect(page.getByTestId('faixa-da-falha-no-envio').getByTestId('status-da-ordem')).toHaveText('Sem confirmação');
  await databaseResume;
  shouldFailOrderListReads = false;

  await fillOrderTicket(page, 'VALE3', '1', '1,00');
  const secondOrderResponse = page.waitForResponse(
    (httpResponse) => httpResponse.request().method() === 'POST' && new URL(httpResponse.url()).pathname === CREATE_ORDER_ROUTE,
  );
  await page.getByRole('button', { name: /^Enviar ordem/ }).click();
  expect((await secondOrderResponse).status()).toBe(200);
  const screenReadsAfterSecondSend = watchScreenReads(page);
  await expect(page.getByTestId('caixa-de-resposta').getByTestId('status-da-ordem')).toHaveText('Aceita');
  // Only the read of the second send itself: the first cycle (reads at about 2 s and 4 s) was cancelled.
  await page.waitForTimeout(7_000);
  expect(screenReadsAfterSecondSend.filter((screenRead) => screenRead.route === ORDERS_ROUTE)).toHaveLength(1);
  expect(screenReadsAfterSecondSend.filter((screenRead) => screenRead.route === EXPOSURES_ROUTE)).toHaveLength(1);
});
