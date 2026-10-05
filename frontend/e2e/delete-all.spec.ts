import { expect, test, type Locator, type Page, type Request } from '@playwright/test';
import path from 'node:path';
import { CREATE_ORDER_ROUTE, EXPOSURES_ROUTE, ORDERS_NOT_DELETED_MESSAGE, ORDERS_ROUTE } from '../src/services/ordersService';

// Talks to the real OrderGenerator and OrderAccumulator. Each test starts and ends with an empty database,
// and only the failure scenarios replace the DELETE response on the network.

const EVIDENCE_FOLDER = process.env.DELETE_ALL_EVIDENCE_FOLDER ?? path.resolve('test-results', 'delete-all-evidence');
const DIALOG_TEXT = 'Todas as ordens serão apagadas e a exposição de PETR4, VALE3 e VIIA4 volta para R$ 0,00. Isso não pode ser desfeito.';
const EMPTY_LIST_TEXT = 'Nenhuma ordem enviada ainda. Preencha a boleta e envie para ver a resposta aqui.';
const CORAL_COLOR = 'rgb(255, 164, 151)';
const TEXT_ON_CORAL_COLOR = 'rgb(42, 14, 10)';
const TRANSLUCENT_CORAL_BORDER_COLOR = 'rgba(255, 164, 151, 0.38)';
const CANCEL_BACKGROUND_COLOR = 'rgb(11, 23, 25)';
const EXPOSURE_SYMBOLS = ['PETR4', 'VALE3', 'VIIA4'];
const TEST_BUY_ORDERS = [
  { symbol: 'PETR4', side: 'buy', quantity: 100, price: 10 },
  { symbol: 'VALE3', side: 'buy', quantity: 200, price: 5 },
  { symbol: 'VIIA4', side: 'buy', quantity: 300, price: 2 },
];

type ExposureOnServer = { symbol: string; exposure: number; remaining: number };

async function deleteAllOrdersOnServer(ticketPage: Page) {
  const deletionResponse = await ticketPage.request.delete(ORDERS_ROUTE);
  expect(deletionResponse.status()).toBe(204);
}

async function createBuyOrdersThroughApi(ticketPage: Page) {
  for (const buyOrder of TEST_BUY_ORDERS) {
    const orderCreationResponse = await ticketPage.request.post(CREATE_ORDER_ROUTE, { data: buyOrder });
    expect(orderCreationResponse.status()).toBe(200);
    expect(((await orderCreationResponse.json()) as { data: { status: string } }).data.status).toBe('accepted');
  }
}

async function countOrdersOnServer(ticketPage: Page) {
  const orderListResponse = await ticketPage.request.get(ORDERS_ROUTE + '?page=1');
  expect(orderListResponse.status()).toBe(200);
  return ((await orderListResponse.json()) as { data: { total: number } }).data.total;
}

async function readExposuresOnServer(ticketPage: Page) {
  const exposuresResponse = await ticketPage.request.get(EXPOSURES_ROUTE);
  expect(exposuresResponse.status()).toBe(200);
  return ((await exposuresResponse.json()) as { data: { exposures: ExposureOnServer[] } }).data.exposures;
}

// With the page at 90%, the browser rounds the computed size (38px becomes 37.9861px); the tolerance is five hundredths of a pixel.
async function checkCssPixelSize(screenElement: Locator, cssProperty: string, expectedSizeInPx: number) {
  await expect
    .poll(
      async () =>
        parseFloat(await screenElement.evaluate((elementOnPage, propertyName) => getComputedStyle(elementOnPage).getPropertyValue(propertyName), cssProperty)),
      { message: cssProperty },
    )
    .toBeCloseTo(expectedSizeInPx, 1);
}

// Strokes of TrashIcon and AlertIcon in Icons.tsx: the right icon is proven by its drawing, not by counting svg elements.
const TRASH_ICON_DRAWING = ['M4 7h16', 'M10 11v6M14 11v6', 'M6 7l1 13h10l1-13', 'M9 7V4h6v3'];
const ALERT_ICON_DRAWING = ['M10.3 3.9L2.4 17.5a2 2 0 001.7 3h15.8a2 2 0 001.7-3L13.7 3.9a2 2 0 00-3.4 0z', 'M12 9v4', 'M12 17h.01'];

async function checkIconDrawing(elementWithIcon: Locator, expectedIconStrokes: string[]) {
  await expect(elementWithIcon.locator('svg')).toHaveCount(1);
  await expect(elementWithIcon.locator('svg')).toBeVisible();
  await expect(elementWithIcon.locator('svg')).toHaveAttribute('aria-hidden', 'true');
  const iconStrokes = await elementWithIcon.locator('svg path').evaluateAll((svgPaths) => svgPaths.map((svgPath) => svgPath.getAttribute('d')));
  expect(iconStrokes).toEqual(expectedIconStrokes);
}

function isApiCall(screenRequest: Request, httpMethod: string, apiRoute: string) {
  return screenRequest.method() === httpMethod && new URL(screenRequest.url()).pathname === apiRoute;
}

function locateOrderListCard(ticketPage: Page) {
  return ticketPage.getByRole('region', { name: 'Compra/Venda' });
}

function locateHeaderDeleteAllButton(ticketPage: Page) {
  return locateOrderListCard(ticketPage).getByRole('button', { name: 'Deletar tudo' });
}

function locateConfirmationDialog(ticketPage: Page) {
  return ticketPage.getByRole('dialog', { name: 'Deletar todos os dados?' });
}

function locateOrderListRows(ticketPage: Page) {
  return locateOrderListCard(ticketPage).getByTestId('linha-da-ordem');
}

async function openScreenWithTestOrders(ticketPage: Page) {
  await createBuyOrdersThroughApi(ticketPage);
  await ticketPage.goto('/');
  await expect(locateOrderListRows(ticketPage)).toHaveCount(TEST_BUY_ORDERS.length);
  await expect(ticketPage.getByTestId('exposicao-PETR4').getByTestId('exposicao-atual')).toHaveText('R$ 1.000,00');
}

async function readExposuresOnScreen(ticketPage: Page) {
  const exposuresOnScreen: string[] = [];
  for (const exposureSymbol of EXPOSURE_SYMBOLS) {
    const assetCard = ticketPage.getByTestId('exposicao-' + exposureSymbol);
    exposuresOnScreen.push(
      `${exposureSymbol} ${await assetCard.getByTestId('exposicao-atual').innerText()} ${await assetCard.getByTestId('exposicao-restante').innerText()}`,
    );
  }
  return exposuresOnScreen;
}

async function checkScreenWasCleared(ticketPage: Page) {
  await expect(locateOrderListCard(ticketPage).getByTestId('lista-de-ordens-vazia')).toHaveText(EMPTY_LIST_TEXT);
  await expect(locateOrderListRows(ticketPage)).toHaveCount(0);
  await expect(ticketPage.getByRole('group', { name: 'Páginas da lista de ordens' })).toHaveCount(0);
  for (const exposureSymbol of EXPOSURE_SYMBOLS) {
    const assetCard = ticketPage.getByTestId('exposicao-' + exposureSymbol);
    await expect(assetCard.getByTestId('exposicao-atual'), exposureSymbol).toHaveText('R$ 0,00');
    await expect(assetCard.getByTestId('exposicao-restante'), exposureSymbol).toHaveText('R$ 100.000.000,00');
    await expect(assetCard.getByTestId('uso-do-limite-porcentagem'), exposureSymbol).toHaveText('0%');
  }
}

// Marks the page to prove the screen did not reload: a reload wipes the mark.
async function markPageWithoutReload(ticketPage: Page) {
  await ticketPage.evaluate(() => {
    (window as unknown as { noReloadPageMark: string }).noReloadPageMark = 'same-page';
  });
}

async function readPageMark(ticketPage: Page) {
  return ticketPage.evaluate(() => (window as unknown as { noReloadPageMark?: string }).noReloadPageMark);
}

test.beforeEach(async ({ page }) => {
  await deleteAllOrdersOnServer(page);
});

test.afterEach(async ({ page }) => {
  await deleteAllOrdersOnServer(page);
});

test('CA-17: the red button with a trash can sits on the right of the Compra/Venda header, 38 px tall with a translucent coral border', async ({ page }) => {
  await page.goto('/');
  const headerButton = locateHeaderDeleteAllButton(page);
  await expect(headerButton).toHaveCount(1);
  await expect(headerButton).toHaveText('Deletar tudo');
  await checkIconDrawing(headerButton, TRASH_ICON_DRAWING);
  await checkCssPixelSize(headerButton, 'height', 38);
  await expect(headerButton).toHaveCSS('color', CORAL_COLOR);
  await expect(headerButton).toHaveCSS('border-top-color', TRANSLUCENT_CORAL_BORDER_COLOR);
  await expect(headerButton).toHaveCSS('background-color', 'rgba(255, 138, 122, 0.12)');
  await headerButton.hover();
  await expect(headerButton).toHaveCSS('background-color', 'rgba(255, 138, 122, 0.2)');
  const titleBox = (await locateOrderListCard(page).getByRole('heading', { name: 'Compra/Venda' }).boundingBox())!;
  const buttonBox = (await headerButton.boundingBox())!;
  const cardBox = (await locateOrderListCard(page).boundingBox())!;
  expect(buttonBox.x).toBeGreaterThan(titleBox.x + titleBox.width);
  // On the right: the button ends less than 40 CSS px from the card's right edge (the page is at 90%).
  expect((cardBox.x + cardBox.width - (buttonBox.x + buttonBox.width)) / 0.9).toBeLessThan(40);
});

test('CA-17: clicking opens the dialog on top, with a darkened and blurred backdrop, alert icon, title, text and the two mockup buttons', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await openScreenWithTestOrders(page);
  await locateHeaderDeleteAllButton(page).click();
  const confirmationDialog = locateConfirmationDialog(page);
  await expect(confirmationDialog).toBeVisible();
  // getByRole({ name }) matches part of the name: the exact text of title and buttons is checked separately.
  const dialogTitle = confirmationDialog.getByRole('heading');
  await expect(dialogTitle).toHaveCount(1);
  await expect(dialogTitle).toHaveText('Deletar todos os dados?');
  await expect(dialogTitle).toHaveCSS('font-family', /^Sora/);
  await checkCssPixelSize(dialogTitle, 'font-size', 20);
  await expect(confirmationDialog.getByRole('button')).toHaveText(['Cancelar', 'Deletar tudo']);
  await expect(confirmationDialog.locator('#delete-confirmation-text')).toHaveText(DIALOG_TEXT);
  await checkCssPixelSize(confirmationDialog, 'width', 440);
  await checkCssPixelSize(confirmationDialog, 'border-top-left-radius', 22);
  const iconSquare = confirmationDialog.locator('.confirmation-dialog-icon');
  await checkIconDrawing(iconSquare, ALERT_ICON_DRAWING);
  await checkCssPixelSize(iconSquare, 'width', 48);
  await checkCssPixelSize(iconSquare, 'height', 48);
  await expect(iconSquare).toHaveCSS('color', CORAL_COLOR);
  const cancelButton = confirmationDialog.getByRole('button', { name: 'Cancelar' });
  const confirmButton = confirmationDialog.getByRole('button', { name: 'Deletar tudo' });
  await checkCssPixelSize(cancelButton, 'height', 46);
  await expect(cancelButton).toHaveCSS('background-color', CANCEL_BACKGROUND_COLOR);
  await checkCssPixelSize(confirmButton, 'height', 46);
  await expect(confirmButton).toHaveCSS('background-color', CORAL_COLOR);
  await expect(confirmButton).toHaveCSS('color', TEXT_ON_CORAL_COLOR);
  const dialogBackdrop = await confirmationDialog.evaluate((dialogOnPage) => {
    const backdropStyle = getComputedStyle(dialogOnPage, '::backdrop');
    return { backdropColor: backdropStyle.backgroundColor, backdropFilter: backdropStyle.backdropFilter };
  });
  expect(dialogBackdrop).toEqual({ backdropColor: 'rgba(4, 10, 11, 0.62)', backdropFilter: 'blur(6px)' });
  // On top of the screen: the dialog center is the topmost element at that point, and the header button stays behind it.
  const dialogCenterIsInsideDialog = await confirmationDialog.evaluate((dialogOnPage) => {
    const dialogBox = dialogOnPage.getBoundingClientRect();
    return dialogOnPage.contains(document.elementFromPoint(dialogBox.x + dialogBox.width / 2, dialogBox.y + dialogBox.height / 2));
  });
  expect(dialogCenterIsInsideDialog).toBe(true);
  await expect(confirmationDialog.getByRole('button', { name: 'Cancelar' })).toBeFocused();
  await page.screenshot({ path: path.join(EVIDENCE_FOLDER, '08-open-dialog-1440.png') });
});

for (const dialogCloseMethod of ['Cancel button', 'Esc key', 'click outside the dialog'] as const) {
  test(`CA-18: ${dialogCloseMethod} closes the dialog without deleting anything — list and exposure unchanged`, async ({ page }) => {
    await openScreenWithTestOrders(page);
    const exposuresBefore = await readExposuresOnScreen(page);
    const deleteCalls: string[] = [];
    page.on('request', (screenRequest) => {
      if (isApiCall(screenRequest, 'DELETE', ORDERS_ROUTE)) deleteCalls.push(screenRequest.url());
    });
    await locateHeaderDeleteAllButton(page).click();
    await expect(locateConfirmationDialog(page)).toBeVisible();

    if (dialogCloseMethod === 'Cancel button') await locateConfirmationDialog(page).getByRole('button', { name: 'Cancelar' }).click();
    if (dialogCloseMethod === 'Esc key') await page.keyboard.press('Escape');
    if (dialogCloseMethod === 'click outside the dialog') await page.mouse.click(8, 8);

    await expect(locateConfirmationDialog(page)).toHaveCount(0);
    await expect(locateHeaderDeleteAllButton(page)).toBeFocused();
    await expect(locateOrderListRows(page)).toHaveCount(TEST_BUY_ORDERS.length);
    expect(await readExposuresOnScreen(page)).toEqual(exposuresBefore);
    expect(await countOrdersOnServer(page)).toBe(TEST_BUY_ORDERS.length);
    expect(deleteCalls).toEqual([]);
  });
}

test('CA-19 and CA-41: Deletar tudo in the dialog clears the database, rereads only page 1 and the exposure, and shows the cleared screen without reloading', async ({ page }) => {
  await openScreenWithTestOrders(page);
  await markPageWithoutReload(page);
  // Counting starts at the DELETE itself: every API call after it is included, even a duplicate one.
  const callsAfterDelete: string[] = [];
  let deleteWasSent = false;
  page.on('request', (screenRequest) => {
    if (isApiCall(screenRequest, 'DELETE', ORDERS_ROUTE)) {
      deleteWasSent = true;
      return;
    }
    if (deleteWasSent && new URL(screenRequest.url()).pathname.startsWith('/api/')) {
      const callUrl = new URL(screenRequest.url());
      callsAfterDelete.push(`${screenRequest.method()} ${callUrl.pathname}${callUrl.search}`);
    }
  });
  await locateHeaderDeleteAllButton(page).click();
  const deleteResponse = page.waitForResponse((httpResponse) => isApiCall(httpResponse.request(), 'DELETE', ORDERS_ROUTE));
  await locateConfirmationDialog(page).getByRole('button', { name: 'Deletar tudo' }).click();
  expect((await deleteResponse).status()).toBe(204);

  await expect(locateConfirmationDialog(page)).toHaveCount(0);
  await checkScreenWasCleared(page);
  expect(callsAfterDelete.sort()).toEqual(['GET /api/exposures', 'GET /api/orders?page=1']);
  expect(await readPageMark(page)).toBe('same-page');
  expect(await countOrdersOnServer(page)).toBe(0);
  expect(await readExposuresOnServer(page)).toEqual(
    EXPOSURE_SYMBOLS.map((exposureSymbol) => ({ symbol: exposureSymbol, exposure: 0, remaining: 100_000_000 })),
  );
  await page.screenshot({ path: path.join(EVIDENCE_FOLDER, '08-after-delete-1440.png') });
});

test('CA-45: with a 503 and nothing deleted on the server, the screen rereads list and exposure before showing the error, and nothing changes', async ({ page }) => {
  await openScreenWithTestOrders(page);
  const exposuresBefore = await readExposuresOnScreen(page);
  await page.route((callUrl) => callUrl.pathname === ORDERS_ROUTE, async (interceptedRoute) => {
    if (interceptedRoute.request().method() !== 'DELETE') return interceptedRoute.fallback();
    await interceptedRoute.fulfill({ status: 503, contentType: 'application/problem+json', body: JSON.stringify({ type: 'urn:base-investimentos:problem:order-accumulator-unavailable', title: 'Serviço indisponível', status: 503, detail: 'Não foi possível falar com o OrderAccumulator. Tente de novo em instantes.', success: false, statusResultado: 'ServiceUnavailable', errors: [] }) });
  });
  const rereadControl = await holdRereadsAfterDelete(page);

  await locateHeaderDeleteAllButton(page).click();
  await locateConfirmationDialog(page).getByRole('button', { name: 'Deletar tudo' }).click();
  await rereadControl.rereadsArrived;
  // While list and exposure have not come back, the error does not show yet.
  await expect(locateConfirmationDialog(page).getByRole('alert')).toHaveCount(0);
  rereadControl.releaseRereads();

  await expect(locateConfirmationDialog(page).getByRole('alert')).toHaveText(ORDERS_NOT_DELETED_MESSAGE);
  await expect(locateConfirmationDialog(page)).toBeVisible();
  await expect(locateOrderListRows(page)).toHaveCount(TEST_BUY_ORDERS.length);
  expect(await readExposuresOnScreen(page)).toEqual(exposuresBefore);
  expect(await countOrdersOnServer(page)).toBe(TEST_BUY_ORDERS.length);
});

test('CA-45: with a 503 after the server deleted (OrderGenerator deadline), the reread shows the cleared screen and the dialog shows the error', async ({ page }) => {
  await openScreenWithTestOrders(page);
  await page.route((callUrl) => callUrl.pathname === ORDERS_ROUTE, async (interceptedRoute) => {
    if (interceptedRoute.request().method() !== 'DELETE') return interceptedRoute.fallback();
    const serverResponse = await interceptedRoute.fetch();
    expect(serverResponse.status()).toBe(204);
    await interceptedRoute.fulfill({ status: 503, contentType: 'application/problem+json', body: JSON.stringify({ type: 'urn:base-investimentos:problem:order-accumulator-unavailable', title: 'Serviço indisponível', status: 503, detail: 'Não foi possível falar com o OrderAccumulator. Tente de novo em instantes.', success: false, statusResultado: 'ServiceUnavailable', errors: [] }) });
  });
  const rereadControl = await holdRereadsAfterDelete(page);

  await locateHeaderDeleteAllButton(page).click();
  await locateConfirmationDialog(page).getByRole('button', { name: 'Deletar tudo' }).click();
  await rereadControl.rereadsArrived;
  // While list and exposure have not come back, the error does not show yet.
  await expect(locateConfirmationDialog(page).getByRole('alert')).toHaveCount(0);
  rereadControl.releaseRereads();

  await expect(locateConfirmationDialog(page).getByRole('alert')).toHaveText(ORDERS_NOT_DELETED_MESSAGE);
  await checkScreenWasCleared(page);
  expect(await countOrdersOnServer(page)).toBe(0);
});

test('ASSUMI-04: with the screen on page 2, the 503 on delete rereads page 2 itself and the exposure before the error', async ({ page }) => {
  for (let orderPosition = 0; orderPosition < 13; orderPosition++) {
    const orderCreationResponse = await page.request.post(CREATE_ORDER_ROUTE, { data: { symbol: 'PETR4', side: 'buy', quantity: 1, price: 1 } });
    expect(orderCreationResponse.status()).toBe(200);
  }
  await page.goto('/');
  await expect(locateOrderListRows(page)).toHaveCount(10);
  await page.getByRole('group', { name: 'Páginas da lista de ordens' }).getByRole('button', { name: 'Página 2', exact: true }).click();
  await expect(locateOrderListRows(page)).toHaveCount(3);
  await page.route((callUrl) => callUrl.pathname === ORDERS_ROUTE, async (interceptedRoute) => {
    if (interceptedRoute.request().method() !== 'DELETE') return interceptedRoute.fallback();
    await interceptedRoute.fulfill({ status: 503, contentType: 'application/problem+json', body: JSON.stringify({ type: 'urn:base-investimentos:problem:order-accumulator-unavailable', title: 'Serviço indisponível', status: 503, detail: 'Não foi possível falar com o OrderAccumulator. Tente de novo em instantes.', success: false, statusResultado: 'ServiceUnavailable', errors: [] }) });
  });
  const rereadControl = await holdRereadsAfterDelete(page);

  await locateHeaderDeleteAllButton(page).click();
  await locateConfirmationDialog(page).getByRole('button', { name: 'Deletar tudo' }).click();
  await rereadControl.rereadsArrived;
  // The reread asks for page 2 itself, and the error waits for list and exposure to come back.
  expect(rereadControl.readPageRequestedOnReread()).toBe('2');
  await expect(locateConfirmationDialog(page).getByRole('alert')).toHaveCount(0);
  rereadControl.releaseRereads();

  await expect(locateConfirmationDialog(page).getByRole('alert')).toHaveText(ORDERS_NOT_DELETED_MESSAGE);
  await expect(locateOrderListRows(page)).toHaveCount(3);
  await expect(page.getByRole('group', { name: 'Páginas da lista de ordens' }).getByRole('button', { name: 'Página 2', exact: true })).toHaveAttribute('aria-current', 'page');
});

test('RF-08: while the delete is on the server, both dialog buttons are disabled and only one DELETE goes out', async ({ page }) => {
  await openScreenWithTestOrders(page);
  let releaseDelete: () => void = () => {};
  const deleteReleased = new Promise<void>((resolveRelease) => (releaseDelete = resolveRelease));
  let deleteRequestCount = 0;
  await page.route((callUrl) => callUrl.pathname === ORDERS_ROUTE, async (interceptedRoute) => {
    if (interceptedRoute.request().method() !== 'DELETE') return interceptedRoute.fallback();
    deleteRequestCount++;
    await deleteReleased;
    await interceptedRoute.continue();
  });

  await locateHeaderDeleteAllButton(page).click();
  const confirmationDialog = locateConfirmationDialog(page);
  await confirmationDialog.getByRole('button', { name: 'Deletar tudo' }).click();
  await expect(confirmationDialog.getByRole('button', { name: 'Apagando…' })).toBeDisabled();
  await expect(confirmationDialog.getByRole('button', { name: 'Cancelar' })).toBeDisabled();
  await expect(confirmationDialog.getByRole('button', { name: 'Apagando…' })).toHaveCSS('opacity', '0.7');
  await expect(confirmationDialog.getByRole('button', { name: 'Cancelar' })).toHaveCSS('opacity', '0.7');
  // Two Esc presses in a row: in Chrome the second one no longer goes through the <dialog> "cancel".
  await page.keyboard.press('Escape');
  await page.keyboard.press('Escape');
  await page.mouse.click(8, 8);
  await expect(confirmationDialog).toBeVisible();
  await confirmationDialog.getByRole('button', { name: 'Apagando…' }).click({ force: true });
  releaseDelete();

  await expect(confirmationDialog).toHaveCount(0);
  await checkScreenWasCleared(page);
  expect(deleteRequestCount).toBe(1);
});

test('RNF-05: the dialog is modal and Tab never takes focus to a control outside it', async ({ page }) => {
  await openScreenWithTestOrders(page);
  await locateHeaderDeleteAllButton(page).click();
  const confirmationDialog = locateConfirmationDialog(page);
  await expect(confirmationDialog).toBeVisible();
  expect(await confirmationDialog.evaluate((dialogOnPage) => dialogOnPage.matches(':modal'))).toBe(true);
  await expect(confirmationDialog).toHaveAttribute('aria-labelledby', 'delete-confirmation-title');
  const focusTargetsByTab: string[] = [];
  for (const navigationKey of ['Tab', 'Tab', 'Tab', 'Tab', 'Tab', 'Shift+Tab', 'Shift+Tab', 'Shift+Tab', 'Shift+Tab']) {
    await page.keyboard.press(navigationKey);
    // Outside the dialog controls, Chromium only lets focus go to its own browser bar (activeElement = body).
    focusTargetsByTab.push(
      await page.evaluate(() => {
        const focusedElement = document.activeElement;
        if (!focusedElement || focusedElement === document.body) return 'browser bar';
        return focusedElement.closest('dialog') ? `dialog: ${focusedElement.textContent?.trim()}` : `OUTSIDE: ${focusedElement.outerHTML.slice(0, 80)}`;
      }),
    );
  }
  expect(focusTargetsByTab.filter((focusTargetAfterTab) => focusTargetAfterTab.startsWith('OUTSIDE'))).toEqual([]);
  expect(focusTargetsByTab).toContain('dialog: Cancelar');
  expect(focusTargetsByTab).toContain('dialog: Deletar tudo');
});

test('ASSUMI-05: opening the dialog again after an error starts without the old message', async ({ page }) => {
  await openScreenWithTestOrders(page);
  await page.route((callUrl) => callUrl.pathname === ORDERS_ROUTE, async (interceptedRoute) => {
    if (interceptedRoute.request().method() !== 'DELETE') return interceptedRoute.fallback();
    await interceptedRoute.fulfill({ status: 503, contentType: 'application/json', body: '{}' });
  });
  await locateHeaderDeleteAllButton(page).click();
  await locateConfirmationDialog(page).getByRole('button', { name: 'Deletar tudo' }).click();
  await expect(locateConfirmationDialog(page).getByRole('alert')).toHaveText(ORDERS_NOT_DELETED_MESSAGE);
  await locateConfirmationDialog(page).getByRole('button', { name: 'Cancelar' }).click();
  await expect(locateConfirmationDialog(page)).toHaveCount(0);

  await locateHeaderDeleteAllButton(page).click();
  await expect(locateConfirmationDialog(page)).toBeVisible();
  await expect(locateConfirmationDialog(page).getByRole('alert')).toHaveCount(0);
});

// Holds the two rereads that go out after the DELETE (the list one and the exposure one), so the test
// can look at the dialog before they come back: the error may only appear after both were applied.
async function holdRereadsAfterDelete(ticketPage: Page) {
  let deleteWasSent = false;
  let pageRequestedOnReread: string | null = null;
  let signalOrderListArrived: () => void = () => {};
  let signalExposureArrived: () => void = () => {};
  let releaseRereads: () => void = () => {};
  const orderListArrived = new Promise<void>((resolveArrival) => (signalOrderListArrived = resolveArrival));
  const exposureArrived = new Promise<void>((resolveArrival) => (signalExposureArrived = resolveArrival));
  const rereadsReleased = new Promise<void>((resolveRelease) => (releaseRereads = resolveRelease));
  ticketPage.on('request', (screenRequest) => {
    if (isApiCall(screenRequest, 'DELETE', ORDERS_ROUTE)) deleteWasSent = true;
  });
  await ticketPage.route((callUrl) => callUrl.pathname === ORDERS_ROUTE && callUrl.searchParams.has('page'), async (interceptedRoute) => {
    if (!deleteWasSent) return interceptedRoute.fallback();
    pageRequestedOnReread = new URL(interceptedRoute.request().url()).searchParams.get('page');
    signalOrderListArrived();
    await rereadsReleased;
    await interceptedRoute.fallback();
  });
  await ticketPage.route((callUrl) => callUrl.pathname === EXPOSURES_ROUTE, async (interceptedRoute) => {
    if (!deleteWasSent) return interceptedRoute.fallback();
    signalExposureArrived();
    await rereadsReleased;
    await interceptedRoute.fallback();
  });
  return {
    rereadsArrived: Promise.all([orderListArrived, exposureArrived]),
    releaseRereads: () => releaseRereads(),
    readPageRequestedOnReread: () => pageRequestedOnReread,
  };
}
