import { expect, test, type Locator, type Page, type Request } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import path from 'node:path';
import { CREATE_ORDER_ROUTE, ORDER_LIST_UNAVAILABLE_MESSAGE, ORDERS_ROUTE } from '../src/services/ordersService';

// Talks to the real OrderGenerator and OrderAccumulator. Each scenario deletes everything through the
// DELETE /api/orders route and creates through the API only the orders it needs. The 1,000-page cap scenario
// replaces the list response with one built here, because 10,001 real orders do not fit in an E2E.

const EVIDENCE_FOLDER = process.env.PAGINATION_EVIDENCE_FOLDER ?? path.resolve('test-results', 'pagination-evidence');
const PAGINATION_ACCESSIBLE_NAME = 'Páginas da lista de ordens';
const CURRENT_PAGE_COLORS = { background: 'rgb(232, 239, 238)', text: 'rgb(8, 17, 19)' };
const FOCUS_RING_COLOR = 'rgb(79, 227, 176)';
const FOCUS_RING_WIDTH_IN_CSS_PX = 2;
const REQUESTED_PAGE_HIGHLIGHT_COLOR = 'rgb(79, 227, 176)';
const TRANSPARENT_COLOR = 'rgba(0, 0, 0, 0)';
const THEME_BORDER_COLOR = 'rgb(28, 48, 52)';
const FOOTER_BACKGROUND_COLOR = 'rgb(11, 23, 25)';
const FOOTER_DIVIDER_COLOR = 'rgb(23, 42, 45)';
const FOOTER_BOTTOM_RADIUS = '16px';
const VIEWPORT_SIZE_AT_1440 = { width: 1440, height: 1000 };
const MINIMUM_TEXT_CONTRAST = 4.5;
const PAGINATION_BUTTON_HEIGHT_IN_CSS_PX = 32;

type ServerStoredOrder = { receivedAt: string; status: string; symbol: string | null; side: string | null; quantity: number; price: number; orderId: string; clOrdId: string };
type ServerOrdersPage = { page: number; pageSize: number; total: number; orders: ServerStoredOrder[] };

async function deleteAllServerOrders(orderTicketPage: Page) {
  const deletionResponse = await orderTicketPage.request.delete(ORDERS_ROUTE);
  expect(deletionResponse.status()).toBe(204);
}

async function createOrdersThroughApi(orderTicketPage: Page, orderCount: number) {
  for (let orderPosition = 0; orderPosition < orderCount; orderPosition++) {
    const creationResponse = await orderTicketPage.request.post(CREATE_ORDER_ROUTE, {
      data: { symbol: 'PETR4', side: 'buy', quantity: 1, price: 1 },
    });
    expect(creationResponse.status()).toBe(200);
  }
}

async function readServerOrdersPage(orderTicketPage: Page, pageNumber: number): Promise<ServerOrdersPage> {
  const listResponse = await orderTicketPage.request.get(`${ORDERS_ROUTE}?page=${pageNumber}`);
  expect(listResponse.status()).toBe(200);
  return ((await listResponse.json()) as { data: ServerOrdersPage }).data;
}

function requestAsksForOrderList(httpRequest: Request) {
  return httpRequest.method() === 'GET' && new URL(httpRequest.url()).pathname === ORDERS_ROUTE;
}

function readRequestedPageFromRequest(httpRequest: Request) {
  return new URL(httpRequest.url()).searchParams.get('page');
}

function waitForOrdersPageRead(orderTicketPage: Page, pageNumber: number) {
  return orderTicketPage.waitForResponse(
    (httpResponse) => requestAsksForOrderList(httpResponse.request()) && readRequestedPageFromRequest(httpResponse.request()) === String(pageNumber),
  );
}

async function openScreenAndWaitForFirstPage(orderTicketPage: Page) {
  const firstPageRead = waitForOrdersPageRead(orderTicketPage, 1);
  await orderTicketPage.goto('/');
  await firstPageRead;
}

function locateOrderListCard(orderTicketPage: Page) {
  return orderTicketPage.getByRole('region', { name: 'Compra/Venda' });
}

function locateOrderListPagination(orderTicketPage: Page) {
  return locateOrderListCard(orderTicketPage).getByRole('group', { name: PAGINATION_ACCESSIBLE_NAME });
}

function locatePageButton(orderTicketPage: Page, pageNumber: number) {
  return locateOrderListPagination(orderTicketPage).getByRole('button', { name: `Página ${pageNumber}`, exact: true });
}

function locatePreviousPageButton(orderTicketPage: Page) {
  return locateOrderListPagination(orderTicketPage).getByRole('button', { name: 'Página anterior' });
}

function locateNextPageButton(orderTicketPage: Page) {
  return locateOrderListPagination(orderTicketPage).getByRole('button', { name: 'Próxima página' });
}

function locateOrderListRows(orderTicketPage: Page) {
  return locateOrderListCard(orderTicketPage).getByTestId('linha-da-ordem');
}

async function goToPageThroughButton(orderTicketPage: Page, paginationButton: Locator, expectedPageNumber: number) {
  const pageRead = waitForOrdersPageRead(orderTicketPage, expectedPageNumber);
  await paginationButton.click();
  await pageRead;
}

async function checkPaginationRow(orderTicketPage: Page, expectedRow: string[]) {
  await expect(locateOrderListPagination(orderTicketPage).locator('ul > li')).toHaveText(expectedRow);
}

async function checkCurrentPage(orderTicketPage: Page, pageNumber: number) {
  const currentPageButton = locatePageButton(orderTicketPage, pageNumber);
  await expect(currentPageButton).toHaveAttribute('aria-current', 'page');
  await expect(locateOrderListPagination(orderTicketPage).locator('[aria-current="page"]')).toHaveCount(1);
  await expect(currentPageButton).toHaveCSS('background-color', CURRENT_PAGE_COLORS.background);
  await expect(currentPageButton).toHaveCSS('color', CURRENT_PAGE_COLORS.text);
}

async function checkRowsMatchServer(orderTicketPage: Page, pageNumber: number) {
  const serverOrdersPage = await readServerOrdersPage(orderTicketPage, pageNumber);
  await expect(locateOrderListRows(orderTicketPage)).toHaveCount(serverOrdersPage.orders.length);
  await expect(locateOrderListRows(orderTicketPage).locator('td[data-column="send-identifier"]')).toHaveText(
    serverOrdersPage.orders.map((serverOrder) => serverOrder.clOrdId),
  );
}

// GET /api/orders answers a DataMessage; the simulated page goes in its "data".
function wrapInSuccessDataMessage(ordersPage: ServerOrdersPage) {
  return { success: true, status: 'Ok', message: 'Página de ordens lida.', data: ordersPage, errors: [], errorCode: null };
}

function buildSimulatedOrdersPage(returnedPage: number, totalOrders: number): ServerOrdersPage {
  const builtOrders: ServerStoredOrder[] = Array.from({ length: 10 }, (_, positionInPage) => {
    const orderNumber = String((returnedPage - 1) * 10 + positionInPage + 1).padStart(32, '0');
    return { receivedAt: '2026-10-04T15:00:00Z', status: 'accepted', symbol: 'PETR4', side: 'buy', quantity: 1, price: 1, orderId: orderNumber, clOrdId: orderNumber };
  });
  return { page: returnedPage, pageSize: 10, total: totalOrders, orders: builtOrders };
}

async function checkRowFitsInCardWithoutPageScroll(orderTicketPage: Page) {
  const pageWidths = await orderTicketPage.evaluate(() => ({ contentWidth: document.documentElement.scrollWidth, visibleWidth: document.documentElement.clientWidth }));
  expect(pageWidths.contentWidth).toBe(pageWidths.visibleWidth);

  const cardBox = await locateOrderListCard(orderTicketPage).boundingBox();
  const buttonRow = locateOrderListPagination(orderTicketPage).locator('ul');
  const rowBox = await buttonRow.boundingBox();
  expect(rowBox!.x).toBeGreaterThanOrEqual(cardBox!.x);
  expect(rowBox!.x + rowBox!.width).toBeLessThanOrEqual(cardBox!.x + cardBox!.width);
  const rowNotClipped = await buttonRow.evaluate((rowElement) => rowElement.scrollWidth <= rowElement.clientWidth);
  expect(rowNotClipped).toBe(true);
  await locateOrderListPagination(orderTicketPage).scrollIntoViewIfNeeded();
  for (const rowButton of await locateOrderListPagination(orderTicketPage).getByRole('button').all()) {
    await expect(rowButton).toBeInViewport();
  }
}

// Disabled shows only as the dimmed symbol: no box and no border on any of the four sides.
async function checkDisabledButtonHasNoBox(disabledButton: Locator) {
  await expect(disabledButton).toBeDisabled();
  for (const borderSide of ['top', 'right', 'bottom', 'left']) {
    await expect(disabledButton).toHaveCSS(`border-${borderSide}-color`, TRANSPARENT_COLOR);
  }
  await expect(disabledButton).toHaveCSS('background-color', TRANSPARENT_COLOR);
}

// The footer is the last strip of the table frame, attached to it, with the same width.
async function checkFooterClosesTableFrame(orderTicketPage: Page) {
  const tableFrame = locateOrderListCard(orderTicketPage).locator('.order-table-frame');
  const paginationFooter = locateOrderListPagination(orderTicketPage);
  const frameBox = await tableFrame.boundingBox();
  const footerBox = await paginationFooter.boundingBox();
  expect(footerBox!.y).toBeCloseTo(frameBox!.y + frameBox!.height, 0);
  expect(footerBox!.x).toBeCloseTo(frameBox!.x, 0);
  expect(footerBox!.width).toBeCloseTo(frameBox!.width, 0);
  await expect(tableFrame).toHaveCSS('border-bottom-style', 'none');
  await expect(tableFrame).toHaveCSS('border-bottom-left-radius', '0px');
  await expect(tableFrame).toHaveCSS('border-bottom-right-radius', '0px');
  await expect(paginationFooter).toHaveCSS('border-left-color', THEME_BORDER_COLOR);
  await expect(paginationFooter).toHaveCSS('border-right-color', THEME_BORDER_COLOR);
  await expect(paginationFooter).toHaveCSS('background-color', FOOTER_BACKGROUND_COLOR);
  await expect(paginationFooter).toHaveCSS('border-top-color', FOOTER_DIVIDER_COLOR);
  await expect(paginationFooter).toHaveCSS('border-bottom-color', THEME_BORDER_COLOR);
  await expect(paginationFooter).toHaveCSS('border-bottom-left-radius', FOOTER_BOTTOM_RADIUS);
  await expect(paginationFooter).toHaveCSS('border-bottom-right-radius', FOOTER_BOTTOM_RADIUS);
}

async function saveEvidenceScreenshot(orderTicketPage: Page, fileName: string) {
  mkdirSync(EVIDENCE_FOLDER, { recursive: true });
  await orderTicketPage.screenshot({ path: path.join(EVIDENCE_FOLDER, fileName), fullPage: true });
}

function calculateColorRelativeLuminance(rgbColor: string) {
  const [red, green, blue] = (rgbColor.match(/\d+(\.\d+)?/g) ?? []).slice(0, 3).map((colorChannel) => {
    const channelFrom0To1 = Number(colorChannel) / 255;
    return channelFrom0To1 <= 0.03928 ? channelFrom0To1 / 12.92 : ((channelFrom0To1 + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * red + 0.7152 * green + 0.0722 * blue;
}

function calculateContrastBetweenColors(textColor: string, backgroundColor: string) {
  const textLuminance = calculateColorRelativeLuminance(textColor);
  const backgroundLuminance = calculateColorRelativeLuminance(backgroundColor);
  return (Math.max(textLuminance, backgroundLuminance) + 0.05) / (Math.min(textLuminance, backgroundLuminance) + 0.05);
}

// Disabled button, "…" and summary have no background of their own: the visible background is the one of the first ancestor with a color.
async function readVisibleColorsOfElement(paginationElement: Locator) {
  return paginationElement.evaluate((screenElement) => {
    let elementWithBackground: Element | null = screenElement;
    let visibleBackgroundColor = 'rgba(0, 0, 0, 0)';
    while (elementWithBackground) {
      const elementBackgroundColor = getComputedStyle(elementWithBackground).backgroundColor;
      if (elementBackgroundColor !== 'rgba(0, 0, 0, 0)' && elementBackgroundColor !== 'transparent') {
        visibleBackgroundColor = elementBackgroundColor;
        break;
      }
      elementWithBackground = elementWithBackground.parentElement;
    }
    return { textColor: getComputedStyle(screenElement).color, backgroundColor: visibleBackgroundColor };
  });
}

async function readPageScale(orderTicketPage: Page) {
  return orderTicketPage.evaluate(() => Number(getComputedStyle(document.documentElement).getPropertyValue('--page-scale')));
}

test.beforeEach(async ({ page }) => {
  await deleteAllServerOrders(page);
});

// The following specs count on the database without the dozens of orders created here.
test.afterEach(async ({ page }) => {
  await deleteAllServerOrders(page);
});

test('ASSUMI-01: with 10 orders the list shows all 10 and no pagination appears', async ({ page }) => {
  await createOrdersThroughApi(page, 10);
  await openScreenAndWaitForFirstPage(page);
  await expect(locateOrderListRows(page)).toHaveCount(10);
  await expect(locateOrderListPagination(page)).toHaveCount(0);
});

test('CA-13: with exactly 11 orders the pagination appears with 2 pages and page 2 shows only the 11th', async ({ page }) => {
  await createOrdersThroughApi(page, 11);
  await openScreenAndWaitForFirstPage(page);
  await expect(page.getByTestId('resumo-da-paginacao')).toHaveText('Mostrando 1–10 de 11 ordens');
  await checkPaginationRow(page, ['‹', '1', '2', '›']);

  await goToPageThroughButton(page, locatePageButton(page, 2), 2);
  await checkCurrentPage(page, 2);
  await expect(page.getByTestId('resumo-da-paginacao')).toHaveText('Mostrando 11–11 de 11 ordens');
  await expect(locateNextPageButton(page)).toBeDisabled();
  await checkRowsMatchServer(page, 2);
});

test('ASSUMI-04: a page that no longer exists (orders deleted elsewhere) leads to the last page that still exists', async ({ page }) => {
  await createOrdersThroughApi(page, 25);
  await openScreenAndWaitForFirstPage(page);
  await goToPageThroughButton(page, locatePageButton(page, 3), 3);
  await checkCurrentPage(page, 3);

  // Another tab deletes everything and stores 12 orders: now only pages 1 and 2 exist.
  await deleteAllServerOrders(page);
  await createOrdersThroughApi(page, 12);
  const pagesRequestedAfterDeletion: string[] = [];
  page.on('request', (httpRequest) => {
    if (requestAsksForOrderList(httpRequest)) pagesRequestedAfterDeletion.push(readRequestedPageFromRequest(httpRequest) ?? 'no-page');
  });
  const lastExistingPageRead = waitForOrdersPageRead(page, 2);
  await locatePageButton(page, 3).click();
  await lastExistingPageRead;

  await checkCurrentPage(page, 2);
  await expect(page.getByTestId('resumo-da-paginacao')).toHaveText('Mostrando 11–12 de 12 ordens');
  await checkPaginationRow(page, ['‹', '1', '2', '›']);
  await checkRowsMatchServer(page, 2);
  expect(pagesRequestedAfterDeletion).toEqual(['3', '2']);
});

test('fast clicks: the slow response of a page requested earlier does not cover the page requested later', async ({ page }) => {
  await createOrdersThroughApi(page, 25);
  await openScreenAndWaitForFirstPage(page);

  let releasePage2Response = () => {};
  const page2ResponseReleased = new Promise<void>((resolvePage2Release) => {
    releasePage2Response = resolvePage2Release;
  });
  await page.route(
    (requestedUrl) => requestedUrl.pathname === ORDERS_ROUTE && requestedUrl.searchParams.get('page') === '2',
    async (page2Route) => {
      await page2ResponseReleased;
      await page2Route.continue();
    },
  );

  const page2Response = waitForOrdersPageRead(page, 2);
  await locatePageButton(page, 2).click();
  await goToPageThroughButton(page, locatePageButton(page, 3), 3);
  await checkCurrentPage(page, 3);
  releasePage2Response();
  await (await page2Response).finished();
  // Checkpoint after the late response: two painted frames guarantee the screen has already handled its
  // body; only then does "page 3 stays" prove the old response was discarded, not that it has not arrived yet.
  await page.evaluate(() => new Promise<void>((resolveAfterFrames) => requestAnimationFrame(() => requestAnimationFrame(() => setTimeout(resolveAfterFrames, 0)))));

  await checkCurrentPage(page, 3);
  await expect(page.getByTestId('resumo-da-paginacao')).toHaveText('Mostrando 21–25 de 25 ordens');
  await checkRowsMatchServer(page, 3);
});

test('RF-12: while the requested page has not arrived, the pagination says it is loading and highlights the requested number', async ({ page }) => {
  await createOrdersThroughApi(page, 25);
  await openScreenAndWaitForFirstPage(page);
  await expect(locateOrderListPagination(page)).toHaveAttribute('aria-busy', 'false');

  let releasePage2Response = () => {};
  const page2ResponseReleased = new Promise<void>((resolvePage2Release) => {
    releasePage2Response = resolvePage2Release;
  });
  await page.route(
    (requestedUrl) => requestedUrl.pathname === ORDERS_ROUTE && requestedUrl.searchParams.get('page') === '2',
    async (page2Route) => {
      await page2ResponseReleased;
      await page2Route.continue();
    },
  );

  const page2Response = waitForOrdersPageRead(page, 2);
  await locatePageButton(page, 2).click();
  await expect(locateOrderListPagination(page)).toHaveAttribute('aria-busy', 'true');
  await expect(page.getByTestId('resumo-da-paginacao')).toHaveText('Carregando a página 2…');
  await expect(locatePageButton(page, 2)).toHaveAttribute('data-loading', 'true');
  await expect(locatePageButton(page, 2)).toHaveCSS('border-color', REQUESTED_PAGE_HIGHLIGHT_COLOR);
  await expect(locatePageButton(page, 2)).toHaveCSS('color', REQUESTED_PAGE_HIGHLIGHT_COLOR);
  await checkCurrentPage(page, 1);

  releasePage2Response();
  await page2Response;
  await expect(locateOrderListPagination(page)).toHaveAttribute('aria-busy', 'false');
  await expect(page.getByTestId('resumo-da-paginacao')).toHaveText('Mostrando 11–20 de 25 ordens');
  await checkCurrentPage(page, 2);
  await expect(locatePageButton(page, 2)).not.toHaveAttribute('data-loading');
});

for (const narrowWidth of [375, 520, 640]) {
  test(`RF-12: at ${narrowWidth}px the loading notice does not change how the footer wraps nor move the tapped button`, async ({ page }) => {
    await page.setViewportSize({ width: narrowWidth, height: 1000 });
    let releasePage2Response = () => {};
    const page2ResponseReleased = new Promise<void>((resolvePage2Release) => {
      releasePage2Response = resolvePage2Release;
    });
    await page.route(
      (requestedUrl) => requestedUrl.pathname === ORDERS_ROUTE,
      async (orderListRoute) => {
        if (orderListRoute.request().method() !== 'GET') return orderListRoute.fallback();
        const requestedPage = Number(new URL(orderListRoute.request().url()).searchParams.get('page'));
        if (requestedPage === 2) await page2ResponseReleased;
        await orderListRoute.fulfill({ json: wrapInSuccessDataMessage(buildSimulatedOrdersPage(requestedPage, 25_000)) });
      },
    );
    await openScreenAndWaitForFirstPage(page);
    await locateOrderListPagination(page).scrollIntoViewIfNeeded();
    const buttonBoxBeforeClick = await locatePageButton(page, 2).boundingBox();
    const footerBoxBeforeClick = await locateOrderListPagination(page).boundingBox();

    const page2Response = waitForOrdersPageRead(page, 2);
    await locatePageButton(page, 2).click();
    await expect(page.getByTestId('resumo-da-paginacao')).toHaveText('Carregando a página 2…');
    const buttonBoxWhileLoading = await locatePageButton(page, 2).boundingBox();
    const footerBoxWhileLoading = await locateOrderListPagination(page).boundingBox();
    expect(Math.abs(buttonBoxWhileLoading!.x - buttonBoxBeforeClick!.x)).toBeLessThanOrEqual(1);
    expect(Math.abs(buttonBoxWhileLoading!.y - buttonBoxBeforeClick!.y)).toBeLessThanOrEqual(1);
    expect(Math.abs(footerBoxWhileLoading!.height - footerBoxBeforeClick!.height)).toBeLessThanOrEqual(1);

    releasePage2Response();
    await page2Response;
    await expect(page.getByTestId('resumo-da-paginacao')).toHaveText('Mostrando 11–20 de 25.000 ordens');
  });
}

test('an error when changing page shows the list notice in the card and removes the pagination', async ({ page }) => {
  await createOrdersThroughApi(page, 15);
  await openScreenAndWaitForFirstPage(page);
  await page.route(
    (requestedUrl) => requestedUrl.pathname === ORDERS_ROUTE && requestedUrl.searchParams.get('page') === '2',
    (page2Route) => page2Route.fulfill({ status: 503, contentType: 'application/problem+json', body: JSON.stringify({ type: 'urn:base-investimentos:problem:order-accumulator-unavailable', title: 'Serviço indisponível', status: 503, detail: 'Não foi possível falar com o OrderAccumulator. Tente de novo em instantes.', success: false, statusResultado: 'ServiceUnavailable', errors: [] }) }),
  );

  await goToPageThroughButton(page, locatePageButton(page, 2), 2);
  await expect(locateOrderListCard(page).getByRole('alert')).toHaveText(ORDER_LIST_UNAVAILABLE_MESSAGE);
  await expect(locateOrderListRows(page)).toHaveCount(0);
  await expect(locateOrderListPagination(page)).toHaveCount(0);
});

test('CA-13: with 23 orders the pagination goes to page 2, to 3 and back to 1 showing the orders of each one', async ({ page }) => {
  await page.setViewportSize(VIEWPORT_SIZE_AT_1440);
  await createOrdersThroughApi(page, 23);
  await openScreenAndWaitForFirstPage(page);

  await expect(locateOrderListPagination(page)).toBeVisible();
  // The pagination is a group of the card, not a menu: the page's "no menu" rule still holds.
  await expect(page.getByRole('navigation')).toHaveCount(0);
  await expect(page.getByTestId('resumo-da-paginacao')).toHaveText('Mostrando 1–10 de 23 ordens');
  await checkPaginationRow(page, ['‹', '1', '2', '3', '›']);
  await checkCurrentPage(page, 1);
  await checkDisabledButtonHasNoBox(locatePreviousPageButton(page));
  await expect(locateNextPageButton(page)).toBeEnabled();
  await expect(locateNextPageButton(page)).toHaveCSS('border-top-color', THEME_BORDER_COLOR);
  await checkRowsMatchServer(page, 1);
  await checkFooterClosesTableFrame(page);
  await saveEvidenceScreenshot(page, '07-pagination-1440.png');

  await goToPageThroughButton(page, locatePageButton(page, 2), 2);
  await checkCurrentPage(page, 2);
  await expect(page.getByTestId('resumo-da-paginacao')).toHaveText('Mostrando 11–20 de 23 ordens');
  await expect(locatePreviousPageButton(page)).toBeEnabled();
  await expect(locateNextPageButton(page)).toBeEnabled();
  await checkRowsMatchServer(page, 2);

  await goToPageThroughButton(page, locateNextPageButton(page), 3);
  await checkCurrentPage(page, 3);
  await expect(page.getByTestId('resumo-da-paginacao')).toHaveText('Mostrando 21–23 de 23 ordens');
  await checkDisabledButtonHasNoBox(locateNextPageButton(page));
  await expect(locatePreviousPageButton(page)).toHaveCSS('border-left-color', THEME_BORDER_COLOR);
  await checkRowsMatchServer(page, 3);

  await goToPageThroughButton(page, locatePreviousPageButton(page), 2);
  await checkCurrentPage(page, 2);
  await goToPageThroughButton(page, locatePageButton(page, 1), 1);
  await checkCurrentPage(page, 1);
  await expect(page.getByTestId('resumo-da-paginacao')).toHaveText('Mostrando 1–10 de 23 ordens');
  await expect(locatePreviousPageButton(page)).toBeDisabled();
  await checkRowsMatchServer(page, 1);

  const pageScale = await readPageScale(page);
  expect(pageScale).toBe(0.9);
  const pageButtonBox = await locatePageButton(page, 2).boundingBox();
  expect((pageButtonBox?.height ?? 0) / pageScale).toBeCloseTo(PAGINATION_BUTTON_HEIGHT_IN_CSS_PX, 0);
});

test('CA-37: with 8 pages the row shortens with a non-clickable "…" and shows the first, the last, the current and its neighbours', async ({ page }) => {
  await page.setViewportSize(VIEWPORT_SIZE_AT_1440);
  await createOrdersThroughApi(page, 75);
  await openScreenAndWaitForFirstPage(page);
  await expect(page.getByTestId('resumo-da-paginacao')).toHaveText('Mostrando 1–10 de 75 ordens');
  await checkPaginationRow(page, ['‹', '1', '2', '…', '8', '›']);

  const paginationEllipsis = locateOrderListPagination(page).getByTestId('reticencias-da-paginacao');
  await expect(paginationEllipsis).toHaveCount(1);
  const ellipsisNature = await paginationEllipsis.evaluate((ellipsisElement) => ({
    tagName: ellipsisElement.tagName,
    controlsInside: ellipsisElement.querySelectorAll('button, a, [tabindex]').length,
    tabOrder: (ellipsisElement as HTMLElement).tabIndex,
  }));
  expect(ellipsisNature).toEqual({ tagName: 'LI', controlsInside: 0, tabOrder: -1 });
  const ellipsisVisibleColors = await readVisibleColorsOfElement(paginationEllipsis);
  expect(calculateContrastBetweenColors(ellipsisVisibleColors.textColor, ellipsisVisibleColors.backgroundColor)).toBeGreaterThanOrEqual(MINIMUM_TEXT_CONTRAST);

  // Clicking the "…" must not request any page: after it, the only read is the one of page 2.
  const readsAfterEllipsisClick: string[] = [];
  page.on('request', (httpRequest) => {
    if (requestAsksForOrderList(httpRequest)) readsAfterEllipsisClick.push(readRequestedPageFromRequest(httpRequest) ?? 'no-page');
  });
  await paginationEllipsis.click();
  await goToPageThroughButton(page, locatePageButton(page, 2), 2);
  expect(readsAfterEllipsisClick).toEqual(['2']);
  await checkPaginationRow(page, ['‹', '1', '2', '3', '…', '8', '›']);

  await goToPageThroughButton(page, locatePageButton(page, 3), 3);
  await checkPaginationRow(page, ['‹', '1', '2', '3', '4', '…', '8', '›']);
  await goToPageThroughButton(page, locatePageButton(page, 4), 4);
  await checkPaginationRow(page, ['‹', '1', '…', '3', '4', '5', '…', '8', '›']);
  await checkCurrentPage(page, 4);
  await expect(page.getByTestId('resumo-da-paginacao')).toHaveText('Mostrando 31–40 de 75 ordens');
  await checkRowsMatchServer(page, 4);

  const cardBox = await locateOrderListCard(page).boundingBox();
  const paginationBox = await locateOrderListPagination(page).boundingBox();
  expect(paginationBox!.x).toBeGreaterThanOrEqual(cardBox!.x);
  expect(paginationBox!.x + paginationBox!.width).toBeLessThanOrEqual(cardBox!.x + cardBox!.width);
  await saveEvidenceScreenshot(page, '07-pagination-ellipsis-1440.png');

  await goToPageThroughButton(page, locatePageButton(page, 8), 8);
  await checkPaginationRow(page, ['‹', '1', '…', '7', '8', '›']);
  await expect(page.getByTestId('resumo-da-paginacao')).toHaveText('Mostrando 71–75 de 75 ordens');
  await expect(locateNextPageButton(page)).toBeDisabled();
  await checkRowsMatchServer(page, 8);
});

test('CA-38: sending an order while on page 2 takes the list back to page 1 with the new order on top, without reloading', async ({ page }) => {
  await createOrdersThroughApi(page, 15);
  await openScreenAndWaitForFirstPage(page);
  await goToPageThroughButton(page, locatePageButton(page, 2), 2);
  await checkCurrentPage(page, 2);
  await page.evaluate(() => {
    (window as unknown as { sameLoadMarker: string }).sameLoadMarker = 'not-reloaded';
  });

  await page.getByRole('group', { name: 'Lado da ordem' }).getByRole('button', { name: 'Venda' }).click();
  await page.getByLabel(/^Quantidade de/).fill('7');
  await page.getByLabel('Preço por ação (R$)').fill('12,34');
  const creationResponse = page.waitForResponse((httpResponse) => httpResponse.request().method() === 'POST' && new URL(httpResponse.url()).pathname === CREATE_ORDER_ROUTE);
  const firstPageRead = waitForOrdersPageRead(page, 1);
  await page.getByRole('button', { name: /^Enviar ordem/ }).click();
  const createdOrderBody = ((await (await creationResponse).json()) as { data: { clOrdId: string } }).data;
  await firstPageRead;

  await checkCurrentPage(page, 1);
  await expect(page.getByTestId('resumo-da-paginacao')).toHaveText('Mostrando 1–10 de 16 ordens');
  const topRow = locateOrderListRows(page).nth(0);
  await expect(topRow.locator('td[data-column="send-identifier"]')).toHaveText(createdOrderBody.clOrdId);
  await expect(topRow.locator('td[data-column="side"]')).toHaveText('Venda');
  await expect(topRow.locator('td[data-column="quantity"]')).toHaveText('7');
  await expect(topRow.locator('td[data-column="price"]')).toHaveText(new Intl.NumberFormat('pt-BR', { style: 'currency', currency: 'BRL' }).format(12.34));
  expect(await page.evaluate(() => (window as unknown as { sameLoadMarker?: string }).sameLoadMarker)).toBe('not-reloaded');
});

test('CA-41: the screen asks the server only for the page it shows, always with the page number', async ({ page }) => {
  await createOrdersThroughApi(page, 25);
  const pagesRequestedFromServer: string[] = [];
  page.on('request', (httpRequest) => {
    if (requestAsksForOrderList(httpRequest)) pagesRequestedFromServer.push(readRequestedPageFromRequest(httpRequest) ?? 'no-page');
  });

  const firstPageResponse = waitForOrdersPageRead(page, 1);
  await page.goto('/');
  const firstPageBody = ((await (await firstPageResponse).json()) as { data: ServerOrdersPage }).data;
  await expect(page.getByTestId('resumo-da-paginacao')).toHaveText('Mostrando 1–10 de 25 ordens');
  expect(firstPageBody.orders).toHaveLength(10);
  expect(firstPageBody.total).toBe(25);
  expect(pagesRequestedFromServer).toEqual(['1']);

  await goToPageThroughButton(page, locatePageButton(page, 3), 3);
  await expect(page.getByTestId('resumo-da-paginacao')).toHaveText('Mostrando 21–25 de 25 ordens');
  await expect(locateOrderListRows(page)).toHaveCount(5);
  expect(pagesRequestedFromServer).toEqual(['1', '3']);
});

test('CA-42: with more than 10,000 orders the pagination stops at page 1000 and shows the real total', async ({ page }) => {
  const TOTAL_ABOVE_CAP = 25_000;
  await page.route(
    (requestedUrl) => requestedUrl.pathname === ORDERS_ROUTE,
    async (interceptedRoute) => {
      if (interceptedRoute.request().method() !== 'GET') return interceptedRoute.fallback();
      const requestedPage = Number(new URL(interceptedRoute.request().url()).searchParams.get('page'));
      await interceptedRoute.fulfill({ json: wrapInSuccessDataMessage(buildSimulatedOrdersPage(requestedPage, TOTAL_ABOVE_CAP)) });
    },
  );

  await openScreenAndWaitForFirstPage(page);
  await expect(page.getByTestId('resumo-da-paginacao')).toHaveText('Mostrando 1–10 de 25.000 ordens');
  await checkPaginationRow(page, ['‹', '1', '2', '…', '1000', '›']);
  await expect(locatePageButton(page, 1001)).toHaveCount(0);

  await goToPageThroughButton(page, locatePageButton(page, 1000), 1000);
  await checkCurrentPage(page, 1000);
  await expect(page.getByTestId('resumo-da-paginacao')).toHaveText('Mostrando 9.991–10.000 de 25.000 ordens');
  await checkPaginationRow(page, ['‹', '1', '…', '999', '1000', '›']);
  await expect(locateNextPageButton(page)).toBeDisabled();
});

test('CA-27: the pagination buttons get visible focus through Tab and have readable contrast', async ({ page }) => {
  await page.setViewportSize(VIEWPORT_SIZE_AT_1440);
  await createOrdersThroughApi(page, 23);
  await openScreenAndWaitForFirstPage(page);

  // On page 1 the ‹ is disabled and stays out of the Tab order; the three buttons with different styles are
  // the current page (light background), a regular number and the ›.
  const page2Button = locatePageButton(page, 2);
  const buttonsCheckedThroughTab = [locatePageButton(page, 1), page2Button, locateNextPageButton(page)];
  const pageScale = await readPageScale(page);
  for (const buttonCheckedThroughTab of buttonsCheckedThroughTab) {
    let reachedButtonThroughTab = false;
    for (let tabPress = 0; tabPress < 80 && !reachedButtonThroughTab; tabPress++) {
      await page.keyboard.press('Tab');
      reachedButtonThroughTab = await buttonCheckedThroughTab.evaluate((buttonElement) => buttonElement === document.activeElement);
    }
    expect(reachedButtonThroughTab).toBe(true);
    await expect(buttonCheckedThroughTab).toHaveCSS('outline-style', 'solid');
    await expect(buttonCheckedThroughTab).toHaveCSS('outline-color', FOCUS_RING_COLOR);
    // The ring is the theme's 2px one; with the page at 90% Chromium rounds it to a whole screen pixel.
    const focusRingWidth = await buttonCheckedThroughTab.evaluate((buttonElement) => parseFloat(getComputedStyle(buttonElement).outlineWidth));
    expect(focusRingWidth).toBeCloseTo(Math.floor(FOCUS_RING_WIDTH_IN_CSS_PX * pageScale) / pageScale, 3);
    if (buttonCheckedThroughTab === page2Button) await saveEvidenceScreenshot(page, '07-pagination-tab-focus-1440.png');
  }

  const checkedPaginationTexts = [
    page2Button,
    locatePageButton(page, 1),
    locatePreviousPageButton(page),
    locateNextPageButton(page),
    page.getByTestId('resumo-da-paginacao'),
  ];
  for (const checkedText of checkedPaginationTexts) {
    const textVisibleColors = await readVisibleColorsOfElement(checkedText);
    expect(calculateContrastBetweenColors(textVisibleColors.textColor, textVisibleColors.backgroundColor)).toBeGreaterThanOrEqual(MINIMUM_TEXT_CONTRAST);
  }
});

for (const viewportWidth of [375, 860, 1440, 1920]) {
  test(`CA-26/CA-37/RNF-06: at ${viewportWidth}px the 8-page and the 1,000-page rows fit in the card, close the frame and the page does not scroll sideways`, async ({ page }) => {
    await page.setViewportSize({ width: viewportWidth, height: 1000 });
    await createOrdersThroughApi(page, 75);
    await openScreenAndWaitForFirstPage(page);
    await goToPageThroughButton(page, locatePageButton(page, 2), 2);
    await goToPageThroughButton(page, locatePageButton(page, 3), 3);
    await goToPageThroughButton(page, locatePageButton(page, 4), 4);
    await checkPaginationRow(page, ['‹', '1', '…', '3', '4', '5', '…', '8', '›']);
    await checkRowFitsInCardWithoutPageScroll(page);
    await checkFooterClosesTableFrame(page);
    await saveEvidenceScreenshot(page, `07-pagination-8-pages-${viewportWidth}.png`);

    // The widest row the screen can show: numbers with 3 and 4 digits (‹ 1 … 499 500 501 … 1000 ›).
    await page.route(
      (requestedUrl) => requestedUrl.pathname === ORDERS_ROUTE,
      (orderListRoute) =>
        orderListRoute.request().method() === 'GET' ? orderListRoute.fulfill({ json: wrapInSuccessDataMessage(buildSimulatedOrdersPage(500, 25_000)) }) : orderListRoute.fallback(),
    );
    await openScreenAndWaitForFirstPage(page);
    await checkPaginationRow(page, ['‹', '1', '…', '499', '500', '501', '…', '1000', '›']);
    await expect(page.getByTestId('resumo-da-paginacao')).toHaveText('Mostrando 4.991–5.000 de 25.000 ordens');
    await checkRowFitsInCardWithoutPageScroll(page);
    await checkFooterClosesTableFrame(page);
    await saveEvidenceScreenshot(page, `07-pagination-1000-pages-${viewportWidth}.png`);
  });
}
