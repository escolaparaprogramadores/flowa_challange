import { expect, test, type Locator, type Page, type Response } from '@playwright/test';
import { CREATE_ORDER_ROUTE, EXPOSURES_ROUTE, ORDERS_ROUTE } from '../src/services/ordersService';

const TEST_MODE_NOTICE = 'Modo de teste: a tela não confere os campos e envia como está. Duplo clique no símbolo para sair.';
const SYMBOLS_IN_ORDER = ['PETR4', 'VALE3', 'VIIA4'];
// Theme warning yellow (base-theme.css --color-warning-text / --color-warning-background) as the browser returns them.
const WARNING_TEXT_COLOR = 'rgb(244, 197, 106)';
const WARNING_BACKGROUND_COLOR = 'rgba(242, 184, 75, 0.14)';
const CARD_COLOR = 'rgb(14, 27, 30)';
const YELLOW_FOCUS_RING = 'rgba(242, 184, 75, 0.22) 0px 0px 0px 3px';

// After every send the screen reads exposure and list page 1 again (useOrdersAndExposures.ts): wait for both,
// so the test ends with every request it caused already answered.
function waitForRereadAfterSend(orderTicketPage: Page) {
  const isRead = (httpResponse: Response, readPath: string) => httpResponse.request().method() === 'GET' && new URL(httpResponse.url()).pathname === readPath;
  return Promise.all([
    orderTicketPage.waitForResponse((httpResponse) => isRead(httpResponse, EXPOSURES_ROUTE)),
    orderTicketPage.waitForResponse((httpResponse) => isRead(httpResponse, ORDERS_ROUTE)),
  ]);
}

function locateOrderTicketForm(orderTicketPage: Page) {
  return orderTicketPage.getByRole('form', { name: 'Boleta de ordem' });
}

function locateSymbolButton(orderTicketPage: Page, orderTicketSymbol: string) {
  return locateOrderTicketForm(orderTicketPage).getByRole('group', { name: 'Símbolo' }).getByRole('button', { name: orderTicketSymbol, exact: true });
}

function locateTestModeSymbolField(orderTicketPage: Page) {
  return locateOrderTicketForm(orderTicketPage).getByRole('textbox', { name: 'Símbolo', exact: true });
}

function locateTestModeBadge(orderTicketPage: Page) {
  return locateOrderTicketForm(orderTicketPage).getByText('Modo de teste', { exact: true });
}

function locateTestModeNotice(orderTicketPage: Page) {
  return locateOrderTicketForm(orderTicketPage).getByText(TEST_MODE_NOTICE, { exact: true });
}

function locateSummaryPrice(orderTicketPage: Page) {
  return locateOrderTicketForm(orderTicketPage).locator('.order-summary-row').filter({ hasText: 'Preço por ação' }).locator('dd');
}

async function readComputedStyle(pageTarget: Locator, styleProperties: string[]) {
  return pageTarget.evaluate((elementOnPage, propertyNames) => {
    const elementStyle = getComputedStyle(elementOnPage);
    return Object.fromEntries(propertyNames.map((propertyName) => [propertyName, elementStyle.getPropertyValue(propertyName)]));
  }, styleProperties);
}

function readRgbaChannels(cssColor: string) {
  const [redChannel, greenChannel, blueChannel, alphaChannel = 1] = (cssColor.match(/\d+(\.\d+)?/g) ?? []).map(Number);
  return { redChannel, greenChannel, blueChannel, alphaChannel };
}

// A translucent background is seen over the card: blend it before measuring the contrast.
function blendOverCard(translucentColor: string) {
  const translucentChannels = readRgbaChannels(translucentColor);
  const cardChannels = readRgbaChannels(CARD_COLOR);
  const blendChannel = (topChannel: number, cardChannel: number) => Math.round(topChannel * translucentChannels.alphaChannel + cardChannel * (1 - translucentChannels.alphaChannel));
  return `rgb(${blendChannel(translucentChannels.redChannel, cardChannels.redChannel)}, ${blendChannel(translucentChannels.greenChannel, cardChannels.greenChannel)}, ${blendChannel(translucentChannels.blueChannel, cardChannels.blueChannel)})`;
}

// WCAG 2 contrast ratio between two "rgb(r, g, b)" colors.
function calculateWcagContrast(textColor: string, backgroundColor: string) {
  const calculateColorLuminance = (rgbColor: string) => {
    const [redChannel, greenChannel, blueChannel] = (rgbColor.match(/\d+(\.\d+)?/g) ?? []).slice(0, 3).map(Number).map((channelFrom0To255) => {
      const channelFrom0To1 = channelFrom0To255 / 255;
      return channelFrom0To1 <= 0.03928 ? channelFrom0To1 / 12.92 : ((channelFrom0To1 + 0.055) / 1.055) ** 2.4;
    });
    return 0.2126 * redChannel + 0.7152 * greenChannel + 0.0722 * blueChannel;
  };
  const [higherLuminance, lowerLuminance] = [calculateColorLuminance(textColor), calculateColorLuminance(backgroundColor)].sort((firstLuminance, secondLuminance) => secondLuminance - firstLuminance);
  return (higherLuminance + 0.05) / (lowerLuminance + 0.05);
}

async function expectNormalModeScreen(orderTicketPage: Page) {
  for (const orderTicketSymbol of SYMBOLS_IN_ORDER) {
    await expect(locateSymbolButton(orderTicketPage, orderTicketSymbol), orderTicketSymbol).toBeVisible();
  }
  await expect(locateTestModeSymbolField(orderTicketPage)).toHaveCount(0);
  await expect(locateTestModeBadge(orderTicketPage)).toHaveCount(0);
  await expect(locateTestModeNotice(orderTicketPage)).toHaveCount(0);
}

// CA-1 and CA-2 rules, typed key by key: the quantity stops at 99999 and the price fills from the right.
async function expectNormalModeFieldRules(orderTicketPage: Page) {
  const quantityField = locateOrderTicketForm(orderTicketPage).getByLabel(/^Quantidade de/);
  await quantityField.fill('');
  await quantityField.pressSequentially('9999999');
  await expect(quantityField).toHaveValue('99999');
  await expect(locateOrderTicketForm(orderTicketPage).getByRole('button', { name: 'Aumentar quantidade' })).toBeDisabled();
  const priceField = locateOrderTicketForm(orderTicketPage).getByLabel('Preço por ação (R$)');
  await priceField.fill('');
  await expect(priceField).toHaveValue('0,00');
  await priceField.press('End');
  await priceField.pressSequentially('2500');
  await expect(priceField).toHaveValue('25,00');
}

test.beforeEach(async ({ page }) => {
  await page.goto('/');
});

for (const doubleClickedSymbol of SYMBOLS_IN_ORDER) {
  test(`CA-7: double click on ${doubleClickedSymbol} turns the test mode on, with badge, notice and a free symbol field`, async ({ page }) => {
    await locateSymbolButton(page, doubleClickedSymbol).dblclick();
    await expect(locateTestModeBadge(page)).toBeVisible();
    await expect(locateTestModeNotice(page)).toBeVisible();
    await expect(locateOrderTicketForm(page).getByRole('group', { name: 'Símbolo' })).toHaveCount(0);
    await expect(locateTestModeSymbolField(page)).toHaveValue(doubleClickedSymbol);
  });
}

test('CA-7: the badge sits beside "Nova ordem" and the notice right below, both in the theme yellow with readable contrast', async ({ page }) => {
  await locateSymbolButton(page, 'PETR4').dblclick();
  const ticketTitle = locateOrderTicketForm(page).getByRole('heading', { name: 'Nova ordem' });
  const titleBox = (await ticketTitle.boundingBox())!;
  const badgeBox = (await locateTestModeBadge(page).boundingBox())!;
  const noticeBox = (await locateTestModeNotice(page).boundingBox())!;
  expect(badgeBox.x).toBeGreaterThan(titleBox.x + titleBox.width);
  expect(Math.abs(badgeBox.y + badgeBox.height / 2 - (titleBox.y + titleBox.height / 2))).toBeLessThanOrEqual(4);
  expect(noticeBox.y).toBeGreaterThanOrEqual(titleBox.y + titleBox.height);
  expect(noticeBox.y).toBeLessThan((await locateOrderTicketForm(page).getByRole('group', { name: 'Lado da ordem' }).boundingBox())!.y);

  const badgeStyle = await readComputedStyle(locateTestModeBadge(page), ['color', 'background-color']);
  expect(badgeStyle).toEqual({ color: WARNING_TEXT_COLOR, 'background-color': WARNING_BACKGROUND_COLOR });
  expect(calculateWcagContrast(badgeStyle.color, blendOverCard(badgeStyle['background-color']))).toBeGreaterThanOrEqual(4.5);
  const noticeColor = (await readComputedStyle(locateTestModeNotice(page), ['color'])).color;
  expect(noticeColor).toBe(WARNING_TEXT_COLOR);
  expect(calculateWcagContrast(noticeColor, CARD_COLOR)).toBeGreaterThanOrEqual(4.5);
});

test('CA-7: the test mode symbol field has a yellow border and a yellow ring on keyboard focus', async ({ page }) => {
  await locateSymbolButton(page, 'VALE3').dblclick();
  const testModeSymbolField = locateTestModeSymbolField(page);
  expect((await readComputedStyle(testModeSymbolField, ['border-top-color']))['border-top-color']).toBe(WARNING_TEXT_COLOR);
  await locateOrderTicketForm(page).getByRole('button', { name: 'Venda', exact: true }).focus();
  await page.keyboard.press('Tab');
  await expect(testModeSymbolField).toBeFocused();
  await expect.poll(async () => (await readComputedStyle(testModeSymbolField, ['box-shadow']))['box-shadow']).toBe(YELLOW_FOCUS_RING);
});

test('CA-7: in test mode symbol, quantity and price take any text, and the summary follows without rounding', async ({ page }) => {
  await locateSymbolButton(page, 'PETR4').dblclick();
  await locateTestModeSymbolField(page).fill('ITUB4');
  const testModeQuantityField = locateOrderTicketForm(page).getByLabel('Quantidade', { exact: true });
  await testModeQuantityField.fill('1,5');
  const priceField = locateOrderTicketForm(page).getByLabel('Preço por ação (R$)');
  await priceField.fill('10,005');
  await expect(locateTestModeSymbolField(page)).toHaveValue('ITUB4');
  await expect(testModeQuantityField).toHaveValue('1,5');
  await expect(priceField).toHaveValue('10,005');
  await expect(locateSummaryPrice(page)).toHaveText('R$ 10,005');
  await expect(page.getByTestId('total-estimado')).toHaveText('R$ 15,0075');

  await testModeQuantityField.fill('');
  await testModeQuantityField.pressSequentially('9999999');
  await expect(testModeQuantityField).toHaveValue('9999999');
  await expect(locateOrderTicketForm(page).getByRole('button', { name: 'Aumentar quantidade' })).toBeEnabled();
  await priceField.fill('abc');
  await expect(priceField).toHaveValue('abc');
  await expect(locateSummaryPrice(page)).toHaveText('—');
  await expect(page.getByTestId('total-estimado')).toHaveText('—');
});

test('CA-7: double click on the symbol field turns the test mode off and brings back the buttons and the CA-1/CA-2 rules', async ({ page }) => {
  await locateSymbolButton(page, 'VIIA4').dblclick();
  await locateTestModeSymbolField(page).fill('ITUB4');
  await locateOrderTicketForm(page).getByLabel('Quantidade', { exact: true }).fill('1,5');
  await locateOrderTicketForm(page).getByLabel('Preço por ação (R$)').fill('10,005');
  await locateTestModeSymbolField(page).dblclick();
  await expectNormalModeScreen(page);
  await expect(locateOrderTicketForm(page).getByLabel(/^Quantidade de/)).toHaveValue('100');
  await expect(locateOrderTicketForm(page).getByLabel('Preço por ação (R$)')).toHaveValue('0,00');
  await expectNormalModeFieldRules(page);
});

test('CA-25: reloading with the test mode on brings back the normal screen, and nothing is kept between reloads', async ({ page }) => {
  await locateSymbolButton(page, 'PETR4').dblclick();
  await expect(locateTestModeBadge(page)).toBeVisible();
  await page.reload();
  await expectNormalModeScreen(page);
  await expectNormalModeFieldRules(page);
  expect(new URL(page.url()).pathname + new URL(page.url()).search + new URL(page.url()).hash).toBe('/');
  expect(await page.evaluate(() => ({ localKeys: localStorage.length, sessionKeys: sessionStorage.length, cookies: document.cookie }))).toEqual({ localKeys: 0, sessionKeys: 0, cookies: '' });
});

test('CA-26: in test mode the screen blocks nothing and the request leaves with the typed values, comma swapped for a dot', async ({ page }) => {
  await locateSymbolButton(page, 'PETR4').dblclick();
  await locateTestModeSymbolField(page).fill('ITUB4');
  await locateOrderTicketForm(page).getByLabel('Quantidade', { exact: true }).fill('1,5');
  await locateOrderTicketForm(page).getByLabel('Preço por ação (R$)').fill('10,005');
  const rereadAfterSend = waitForRereadAfterSend(page);
  const createOrderRequest = page.waitForRequest((httpRequest) => httpRequest.method() === 'POST' && new URL(httpRequest.url()).pathname === CREATE_ORDER_ROUTE);
  await locateOrderTicketForm(page).getByRole('button', { name: 'Enviar ordem de compra' }).click();
  expect((await createOrderRequest).postDataJSON()).toEqual({ symbol: 'ITUB4', side: 'buy', quantity: '1.5', price: '10.005' });
  await expect(locateOrderTicketForm(page).getByRole('alert')).toHaveCount(0);
  await rereadAfterSend;
});

test('CA-26: in test mode a quantity that is not a number still leaves the screen as typed', async ({ page }) => {
  await locateSymbolButton(page, 'VALE3').dblclick();
  await locateOrderTicketForm(page).getByLabel('Quantidade', { exact: true }).fill('abc');
  await locateOrderTicketForm(page).getByLabel('Preço por ação (R$)').fill('10,00');
  const rereadAfterSend = waitForRereadAfterSend(page);
  const createOrderRequest = page.waitForRequest((httpRequest) => httpRequest.method() === 'POST' && new URL(httpRequest.url()).pathname === CREATE_ORDER_ROUTE);
  const createOrderResponse = page.waitForResponse((httpResponse) => httpResponse.request().method() === 'POST' && new URL(httpResponse.url()).pathname === CREATE_ORDER_ROUTE);
  await locateOrderTicketForm(page).getByRole('button', { name: 'Enviar ordem de compra' }).click();
  expect((await createOrderRequest).postDataJSON()).toEqual({ symbol: 'VALE3', side: 'buy', quantity: 'abc', price: '10.00' });
  expect((await createOrderResponse).status()).toBe(400);
  await expect(locateOrderTicketForm(page).getByRole('alert')).toHaveCount(0);
  await rereadAfterSend;
});
