import { expect, test, type Locator, type Page, type Request } from '@playwright/test';
import path from 'node:path';
import { CREATE_ORDER_ROUTE, ORDERS_ROUTE } from '../src/services/ordersService';

// Final check of the whole screen, with all slices together: narrow widths (CA-26), keyboard focus (CA-27),
// screenshots to compare with the mockups (CA-28), no call while idle (CA-34) and nothing external (CA-40, CA-43).

const EVIDENCE_FOLDER = process.env.FINAL_SCREEN_EVIDENCE_FOLDER ?? path.resolve('test-results', 'final-screen-evidence');
const PAGE_SCALE = 0.9;
const ACCENT_COLOR = 'rgb(79, 227, 176)';
// 23 orders make 3 pages, as in mockup-02 ("Mostrando 1-10 de 23 ordens").
const MOCKUP_ORDER_COUNT = 23;
const EXPOSURE_SYMBOLS = ['PETR4', 'VALE3', 'VIIA4'];

async function deleteAllOrdersOnServer(orderTicketPage: Page) {
  const deleteResponse = await orderTicketPage.request.delete(ORDERS_ROUTE);
  expect(deleteResponse.status()).toBe(204);
}

async function createMockupOrdersThroughApi(orderTicketPage: Page) {
  const sampleOrders = [
    { symbol: 'PETR4', side: 'buy', quantity: 100, price: 38.2 },
    { symbol: 'VALE3', side: 'sell', quantity: 200, price: 61.4 },
    { symbol: 'VIIA4', side: 'buy', quantity: 1000, price: 1.2 },
  ];
  for (let orderPosition = 0; orderPosition < MOCKUP_ORDER_COUNT; orderPosition++) {
    const createResponse = await orderTicketPage.request.post(CREATE_ORDER_ROUTE, { data: sampleOrders[orderPosition % sampleOrders.length] });
    expect(createResponse.status()).toBe(200);
  }
}

async function openFullScreen(orderTicketPage: Page) {
  await orderTicketPage.goto('/');
  await expect(orderTicketPage.getByRole('region', { name: 'Compra/Venda' }).getByTestId('linha-da-ordem')).toHaveCount(10);
  await expect(orderTicketPage.getByTestId('exposicao-PETR4').getByTestId('exposicao-atual')).not.toHaveText('');
  await orderTicketPage.evaluate(() => document.fonts.ready);
}

function boxesOverlap(firstBox: { x: number; y: number; width: number; height: number }, secondBox: { x: number; y: number; width: number; height: number }) {
  return (
    firstBox.x < secondBox.x + secondBox.width &&
    secondBox.x < firstBox.x + firstBox.width &&
    firstBox.y < secondBox.y + secondBox.height &&
    secondBox.y < firstBox.y + firstBox.height
  );
}

async function assertVisibleAndReadElementBox(screenElement: Locator, elementName: string) {
  await expect(screenElement, elementName).toHaveCount(1);
  await expect(screenElement, elementName).toBeVisible();
  return (await screenElement.boundingBox())!;
}

test.beforeEach(async ({ page }) => {
  await deleteAllOrdersOnServer(page);
  await createMockupOrdersThroughApi(page);
});

test.afterEach(async ({ page }) => {
  await deleteAllOrdersOnServer(page);
});

for (const viewportWidth of [1920, 1440, 860, 375]) {
  test(`CA-28: screenshot of the final screen at ${viewportWidth} px, without horizontal page scrolling`, async ({ page }) => {
    await page.setViewportSize({ width: viewportWidth, height: 1000 });
    await openFullScreen(page);
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(viewportWidth);
    await page.screenshot({ path: path.join(EVIDENCE_FOLDER, `08-final-screen-${viewportWidth}.png`), fullPage: true });
  });
}

for (const viewportWidth of [860, 375]) {
  test(`CA-26: at ${viewportWidth} px the assets stack one below the other, Nova ordem above Compra/Venda and nothing leaves the screen`, async ({ page }) => {
    await page.setViewportSize({ width: viewportWidth, height: 1000 });
    await openFullScreen(page);
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(viewportWidth);

    const assetBoxes = [];
    for (const exposureSymbol of EXPOSURE_SYMBOLS) assetBoxes.push(await assertVisibleAndReadElementBox(page.getByTestId('exposicao-' + exposureSymbol), exposureSymbol));
    for (let assetPosition = 1; assetPosition < assetBoxes.length; assetPosition++) {
      const upperAsset = assetBoxes[assetPosition - 1];
      const lowerAsset = assetBoxes[assetPosition];
      expect(lowerAsset.y, EXPOSURE_SYMBOLS[assetPosition]).toBeGreaterThanOrEqual(upperAsset.y + upperAsset.height);
      expect(Math.abs(lowerAsset.x - upperAsset.x), EXPOSURE_SYMBOLS[assetPosition]).toBeLessThan(1);
    }

    // Desktop order also on mobile (decision G-1): assets → Nova ordem → Compra/Venda.
    const orderTicketBox = await assertVisibleAndReadElementBox(page.getByRole('form', { name: 'Boleta de ordem' }), 'Nova ordem');
    const orderListBox = await assertVisibleAndReadElementBox(page.getByRole('region', { name: 'Compra/Venda' }), 'Compra/Venda');
    const lastAsset = assetBoxes[assetBoxes.length - 1];
    expect(orderTicketBox.y).toBeGreaterThanOrEqual(lastAsset.y + lastAsset.height);
    expect(orderListBox.y).toBeGreaterThanOrEqual(orderTicketBox.y + orderTicketBox.height);

    // ASSUMI-06 (F6 decision, its ASSUMI-04): instead of scrolling sideways, the list fits entirely in the card.
    // At 860 the table stays whole with its header; at 375 each order becomes a block with the 8 labelled fields.
    const orderTableFrame = page.getByRole('region', { name: 'Compra/Venda' }).locator('.order-table-frame');
    const orderTableFrameBox = await assertVisibleAndReadElementBox(orderTableFrame, 'order list frame');
    expect(orderTableFrameBox.x + orderTableFrameBox.width).toBeLessThanOrEqual(orderListBox.x + orderListBox.width + 0.5);
    const orderTableWidthInFrame = await orderTableFrame.evaluate((frameOnPage) => ({ contentWidth: frameOnPage.scrollWidth, visibleWidth: frameOnPage.clientWidth }));
    expect(orderTableWidthInFrame.contentWidth, 'nothing hidden sideways').toBeLessThanOrEqual(orderTableWidthInFrame.visibleWidth);
    const firstListedOrder = page.getByRole('region', { name: 'Compra/Venda' }).getByTestId('linha-da-ordem').first();
    const firstListedOrderBox = await assertVisibleAndReadElementBox(firstListedOrder, 'first order');
    expect(firstListedOrderBox.x + firstListedOrderBox.width).toBeLessThanOrEqual(orderTableFrameBox.x + orderTableFrameBox.width + 0.5);
    const orderListLayout = await firstListedOrder.evaluate((orderRowOnPage) => ({
      rowDisplay: getComputedStyle(orderRowOnPage).display,
      isHeaderVisible: getComputedStyle(orderRowOnPage.closest('table')!.querySelector('thead')!).display !== 'none',
      fieldLabels: [...orderRowOnPage.querySelectorAll('td')].map((orderField) => getComputedStyle(orderField, '::before').content),
    }));
    const expectedFieldLabels = ['"Data"', '"Status"', '"Ativo"', '"Lado"', '"Quantidade"', '"Preço"', '"Número da ordem"', '"Identificador do envio"'];
    if (viewportWidth === 375) {
      expect(orderListLayout).toEqual({ rowDisplay: 'grid', isHeaderVisible: false, fieldLabels: expectedFieldLabels });
    } else {
      expect(orderListLayout).toEqual({ rowDisplay: 'table-row', isHeaderVisible: true, fieldLabels: Array(8).fill('none') });
    }

    // Datadog links and badge wrap without overlapping the logo and without leaving the screen.
    const logoBox = await assertVisibleAndReadElementBox(page.getByRole('img', { name: 'Base investimentos' }), 'logo');
    const datadogLinks = page.getByRole('list', { name: 'Painéis do Datadog' }).getByRole('link');
    await expect(datadogLinks).toHaveCount(3);
    const topBarElements: Array<[string, Locator]> = [
      ['Datadog link 1', datadogLinks.nth(0)],
      ['Datadog link 2', datadogLinks.nth(1)],
      ['Datadog link 3', datadogLinks.nth(2)],
      ['environment badge', page.locator('.environment-badge')],
    ];
    for (const [elementName, topBarElement] of topBarElements) {
      const elementBox = await assertVisibleAndReadElementBox(topBarElement, elementName);
      expect(boxesOverlap(elementBox, logoBox), `${elementName} over the logo`).toBe(false);
      expect(elementBox.x, elementName).toBeGreaterThanOrEqual(0);
      expect(elementBox.x + elementBox.width, elementName).toBeLessThanOrEqual(viewportWidth);
    }

    // Compra/Venda header: title and "Deletar tudo" fit side by side, without clipping.
    const titleBox = await assertVisibleAndReadElementBox(page.getByRole('heading', { name: 'Compra/Venda' }), 'Compra/Venda title');
    const deleteAllButtonBox = await assertVisibleAndReadElementBox(page.getByRole('region', { name: 'Compra/Venda' }).getByRole('button', { name: 'Deletar tudo' }), 'Deletar tudo');
    expect(boxesOverlap(titleBox, deleteAllButtonBox)).toBe(false);
    expect(deleteAllButtonBox.x + deleteAllButtonBox.width).toBeLessThanOrEqual(orderListBox.x + orderListBox.width);

    // The confirmation dialog also fits entirely.
    await page.getByRole('region', { name: 'Compra/Venda' }).getByRole('button', { name: 'Deletar tudo' }).click();
    const dialogBox = await assertVisibleAndReadElementBox(page.getByRole('dialog', { name: 'Deletar todos os dados?' }), 'dialog');
    expect(dialogBox.x).toBeGreaterThanOrEqual(0);
    expect(dialogBox.x + dialogBox.width).toBeLessThanOrEqual(viewportWidth);
    expect(dialogBox.y).toBeGreaterThanOrEqual(0);
    expect(dialogBox.y + dialogBox.height).toBeLessThanOrEqual(page.viewportSize()!.height);
    for (const buttonName of ['Cancelar', 'Deletar tudo']) {
      const buttonBox = await assertVisibleAndReadElementBox(page.getByRole('dialog').getByRole('button', { name: buttonName }), buttonName);
      expect(buttonBox.x, buttonName).toBeGreaterThanOrEqual(dialogBox.x);
      expect(buttonBox.x + buttonBox.width, buttonName).toBeLessThanOrEqual(dialogBox.x + dialogBox.width);
      expect(buttonBox.y, buttonName).toBeGreaterThanOrEqual(dialogBox.y);
      expect(buttonBox.y + buttonBox.height, buttonName).toBeLessThanOrEqual(dialogBox.y + dialogBox.height);
    }
    await page.screenshot({ path: path.join(EVIDENCE_FOLDER, `08-open-dialog-${viewportWidth}.png`) });
  });
}

// WCAG 2 contrast ratio between two "rgb(r, g, b)" colors.
function calculateWcagContrast(foregroundColor: string, backgroundColor: string) {
  const calculateColorLuminance = (rgbColor: string) => {
    const [redChannel, greenChannel, blueChannel] = (rgbColor.match(/\d+(\.\d+)?/g) ?? []).slice(0, 3).map(Number).map((channelFrom0To255) => {
      const channelFrom0To1 = channelFrom0To255 / 255;
      return channelFrom0To1 <= 0.03928 ? channelFrom0To1 / 12.92 : ((channelFrom0To1 + 0.055) / 1.055) ** 2.4;
    });
    return 0.2126 * redChannel + 0.7152 * greenChannel + 0.0722 * blueChannel;
  };
  const [higherLuminance, lowerLuminance] = [calculateColorLuminance(foregroundColor), calculateColorLuminance(backgroundColor)].sort((firstLuminance, secondLuminance) => secondLuminance - firstLuminance);
  return (higherLuminance + 0.05) / (lowerLuminance + 0.05);
}

// Presses Tab until focus reaches the control; keyboard focus is what turns on :focus-visible.
async function moveFocusByTabTo(orderTicketPage: Page, screenControl: Locator, controlName: string) {
  for (let tabCount = 0; tabCount < 80; tabCount++) {
    if (await screenControl.evaluate((controlOnPage) => controlOnPage === document.activeElement)) return;
    await orderTicketPage.keyboard.press('Tab');
  }
  throw new Error(`Tab did not reach ${controlName}`);
}

async function readControlFocusAndContrast(screenControl: Locator) {
  return screenControl.evaluate((controlOnPage) => {
    // A translucent background (like the 12% coral of "Deletar tudo") blends with the backgrounds below it:
    // stacks the element's layers upwards until the first opaque one and returns the color shown on screen.
    const calculateVisibleBackgroundColor = (startElement: Element | null) => {
      const backgroundLayers: number[][] = [];
      for (let currentElement = startElement; currentElement; currentElement = currentElement.parentElement) {
        const [redChannel, greenChannel, blueChannel, opacity = 1] = (getComputedStyle(currentElement).backgroundColor.match(/[\d.]+/g) ?? []).map(Number);
        if (opacity > 0) backgroundLayers.push([redChannel, greenChannel, blueChannel, opacity]);
        if (opacity >= 1) break;
      }
      const pageColor = (getComputedStyle(document.body).backgroundColor.match(/[\d.]+/g) ?? []).map(Number);
      const blendedColor = backgroundLayers.reduceRight(
        (colorBelow, [redChannel, greenChannel, blueChannel, opacity]) => [
          redChannel * opacity + colorBelow[0] * (1 - opacity),
          greenChannel * opacity + colorBelow[1] * (1 - opacity),
          blueChannel * opacity + colorBelow[2] * (1 - opacity),
        ],
        pageColor.slice(0, 3),
      );
      return `rgb(${blendedColor.map(Math.round).join(', ')})`;
    };
    const controlStyle = getComputedStyle(controlOnPage);
    return {
      outlineStyle: controlStyle.outlineStyle,
      outlineColor: controlStyle.outlineColor,
      outlineWidth: parseFloat(controlStyle.outlineWidth),
      textColor: controlStyle.color,
      controlBackgroundColor: calculateVisibleBackgroundColor(controlOnPage),
      surroundingBackgroundColor: calculateVisibleBackgroundColor(controlOnPage.parentElement),
      hasVisibleFocus: controlOnPage.matches(':focus-visible'),
    };
  });
}

// The theme ring is 2 CSS px and the Datadog links' ring is 3 px (top-bar.css). With the page at 90% Chromium rounds
// to a whole screen pixel: 2px becomes 1 pixel and 3px becomes 2.
async function assertVisibleFocusByTab(orderTicketPage: Page, controlName: string, screenControl: Locator, ringWidthInCssPx = 2) {
  await assertVisibleAndReadElementBox(screenControl, controlName);
  await moveFocusByTabTo(orderTicketPage, screenControl, controlName);
  const focusAndContrast = await readControlFocusAndContrast(screenControl);
  expect(focusAndContrast.hasVisibleFocus, controlName).toBe(true);
  expect(focusAndContrast.outlineStyle, controlName).toBe('solid');
  expect(focusAndContrast.outlineColor, controlName).toBe(ACCENT_COLOR);
  expect(focusAndContrast.outlineWidth, controlName).toBeCloseTo(Math.floor(ringWidthInCssPx * PAGE_SCALE) / PAGE_SCALE, 3);
  // Focus ring against the surrounding background: 3:1 (WCAG 1.4.11). Control text against its background: 4.5:1 (WCAG 1.4.3).
  expect(calculateWcagContrast(focusAndContrast.outlineColor, focusAndContrast.surroundingBackgroundColor), `${controlName}: ring`).toBeGreaterThanOrEqual(3);
  expect(calculateWcagContrast(focusAndContrast.textColor, focusAndContrast.controlBackgroundColor), `${controlName}: text`).toBeGreaterThanOrEqual(4.5);
  const controlBox = (await screenControl.boundingBox())!;
  const screenshotMargin = 24;
  await orderTicketPage.screenshot({
    path: path.join(EVIDENCE_FOLDER, `08-focus-${controlName.toLowerCase().replace(/[^a-z0-9]+/g, '-')}.png`),
    clip: {
      x: Math.max(0, controlBox.x - screenshotMargin),
      y: Math.max(0, controlBox.y - screenshotMargin),
      width: controlBox.width + 2 * screenshotMargin,
      height: controlBox.height + 2 * screenshotMargin,
    },
  });
}

test('CA-27: each new control gets visible focus by Tab, with a ring in the accent color and readable contrast', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 });
  await openFullScreen(page);
  const datadogLinks = page.getByRole('list', { name: 'Painéis do Datadog' }).getByRole('link');
  const symbolGroup = page.getByRole('group', { name: 'Símbolo' });
  const pagination = page.getByRole('group', { name: 'Páginas da lista de ordens' });
  for (const linkPosition of [0, 1, 2]) await assertVisibleFocusByTab(page, `Datadog ${linkPosition + 1}`, datadogLinks.nth(linkPosition), 3);
  const screenControls: Array<[string, Locator]> = [
    ['Deletar tudo', page.getByRole('region', { name: 'Compra/Venda' }).getByRole('button', { name: 'Deletar tudo' })],
    ['Page 2', pagination.getByRole('button', { name: 'Página 2', exact: true })],
    ['Next page', pagination.getByRole('button', { name: 'Próxima página' })],
    ['Symbol PETR4', symbolGroup.getByRole('button', { name: 'PETR4', exact: true })],
    ['Symbol VALE3', symbolGroup.getByRole('button', { name: 'VALE3', exact: true })],
    ['Symbol VIIA4', symbolGroup.getByRole('button', { name: 'VIIA4', exact: true })],
  ];
  for (const [controlName, screenControl] of screenControls) await assertVisibleFocusByTab(page, controlName, screenControl);

  // Dialog buttons: open it from the keyboard, starting at the header's "Deletar tudo".
  await page.getByRole('region', { name: 'Compra/Venda' }).getByRole('button', { name: 'Deletar tudo' }).focus();
  await page.keyboard.press('Enter');
  const confirmationDialog = page.getByRole('dialog', { name: 'Deletar todos os dados?' });
  await expect(confirmationDialog).toBeVisible();
  await assertVisibleFocusByTab(page, 'Dialog Cancelar', confirmationDialog.getByRole('button', { name: 'Cancelar' }));
  await assertVisibleFocusByTab(page, 'Dialog Deletar tudo', confirmationDialog.getByRole('button', { name: 'Deletar tudo' }));
  await page.keyboard.press('Escape');
  await expect(confirmationDialog).toHaveCount(0);
});

test('CA-34: with the full screen idle for 30 s, no new call goes to the server', async ({ page }) => {
  test.setTimeout(60_000);
  await openFullScreen(page);
  await page.waitForLoadState('networkidle');
  const callsWhileIdle: string[] = [];
  page.on('request', (screenRequest) => callsWhileIdle.push(`${screenRequest.method()} ${screenRequest.url()}`));
  await page.waitForTimeout(30_000);
  expect(callsWhileIdle).toEqual([]);
});

test('CA-40 and CA-43: when opening the full screen, nothing goes to an external domain and no image is downloaded (Datadog logo is in the code)', async ({ page, baseURL }) => {
  const requestsOnOpen: Request[] = [];
  page.on('request', (screenRequest) => requestsOnOpen.push(screenRequest));
  await openFullScreen(page);
  await page.waitForLoadState('networkidle');
  const screenOrigin = new URL(baseURL!).origin;
  const externalCalls = requestsOnOpen.map((screenRequest) => screenRequest.url()).filter((calledAddress) => new URL(calledAddress).origin !== screenOrigin);
  expect(externalCalls).toEqual([]);
  const downloadedImages = requestsOnOpen.filter((screenRequest) => screenRequest.resourceType() === 'image').map((screenRequest) => screenRequest.url());
  expect(downloadedImages).toEqual([]);
  expect(requestsOnOpen.filter((screenRequest) => /datadog/i.test(new URL(screenRequest.url()).hostname))).toEqual([]);
  // The Datadog logo of the 3 links is an SVG inside the page.
  await expect(page.getByRole('list', { name: 'Painéis do Datadog' }).locator('.datadog-dashboard-logo svg')).toHaveCount(3);
});
