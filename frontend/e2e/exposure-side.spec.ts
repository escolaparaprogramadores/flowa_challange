import path from 'node:path';
import { expect, test, type Locator, type Page } from '@playwright/test';
import { CREATE_ORDER_ROUTE, EXPOSURES_ROUTE, ORDERS_ROUTE } from '../src/services/ordersService';

// Talks to the real OrderGenerator and OrderAccumulator. Every test starts and ends with "Deletar tudo",
// so the exposures it sees come only from the orders it sent itself.

const EVIDENCE_FOLDER = process.env.EXPOSURE_SIDE_EVIDENCE_FOLDER ?? path.resolve('test-results', 'exposure-side-evidence');
// The badge sits at the top of the card, where its gradient is lightest: the contrast is measured over that end.
const CARD_TOP_BACKGROUND_COLOR = 'rgb(15, 31, 34)';
const ACCENT_COLOR = 'rgb(79, 227, 176)';
const SELL_COLOR = 'rgb(255, 164, 151)';
const AMBER_BAR_GRADIENT = 'linear-gradient(90deg, rgb(233, 167, 60), rgb(244, 197, 106))';
const MIRRORED_DOWN_TRANSFORM = 'matrix(1, 0, 0, -1, 0, 0)';
const PAGE_SCALE = 0.9;

const EXPECTED_SIDE_BADGES = {
  Comprado: { textColor: 'rgb(111, 235, 192)', backgroundColor: 'rgba(79, 227, 176, 0.13)', borderColor: 'rgba(111, 235, 192, 0.38)' },
  Vendido: { textColor: 'rgb(255, 164, 151)', backgroundColor: 'rgba(255, 138, 122, 0.12)', borderColor: 'rgba(255, 164, 151, 0.38)' },
  Zerado: { textColor: 'rgb(157, 176, 174)', backgroundColor: 'rgb(18, 36, 39)', borderColor: 'rgb(46, 75, 80)' },
} as const;

type SideBadgeLabel = keyof typeof EXPECTED_SIDE_BADGES;

type ServerExposure = { symbol: string; exposure: number; remaining: number };

async function deleteAllOrdersOnServer(ticketPage: Page) {
  const deletionResponse = await ticketPage.request.delete(ORDERS_ROUTE);
  expect(deletionResponse.status()).toBe(204);
}

async function sendOrderThroughApi(ticketPage: Page, symbol: string, side: 'buy' | 'sell', quantity: number, price: number) {
  const orderResponse = await ticketPage.request.post(CREATE_ORDER_ROUTE, { data: { symbol, side, quantity, price } });
  expect(orderResponse.status()).toBe(200);
  expect(((await orderResponse.json()) as { data: { status: string } }).data.status).toBe('accepted');
}

async function readServerExposure(ticketPage: Page, exposureSymbol: string) {
  const exposuresResponse = await ticketPage.request.get(EXPOSURES_ROUTE);
  expect(exposuresResponse.status()).toBe(200);
  const serverExposures = ((await exposuresResponse.json()) as { data: { exposures: ServerExposure[] } }).data.exposures;
  const symbolExposure = serverExposures.find((serverExposure) => serverExposure.symbol === exposureSymbol);
  if (!symbolExposure) throw new Error('Symbol ' + exposureSymbol + ' missing from /api/exposures');
  return symbolExposure;
}

async function sendOrderThroughOrderTicket(ticketPage: Page, symbol: string, sideLabel: 'Compra' | 'Venda', quantity: string, price: string) {
  await ticketPage.getByRole('group', { name: 'Símbolo' }).getByRole('button', { name: symbol, exact: true }).click();
  await ticketPage.getByRole('group', { name: 'Lado da ordem' }).getByRole('button', { name: sideLabel }).click();
  await ticketPage.getByLabel(/^Quantidade de/).fill(quantity);
  await ticketPage.getByLabel('Preço por ação (R$)').fill(price);
  const createOrderResponse = ticketPage.waitForResponse(
    (httpResponse) => httpResponse.request().method() === 'POST' && new URL(httpResponse.url()).pathname === CREATE_ORDER_ROUTE,
  );
  const exposuresReread = ticketPage.waitForResponse(
    (httpResponse) => httpResponse.request().method() === 'GET' && new URL(httpResponse.url()).pathname === EXPOSURES_ROUTE,
  );
  await ticketPage.getByRole('button', { name: /^Enviar ordem/ }).click();
  const orderStatus = ((await (await createOrderResponse).json()) as { data: { status: string } }).data.status;
  await exposuresReread;
  await expect(ticketPage.getByRole('button', { name: /^Enviar ordem/ })).toBeEnabled();
  return orderStatus;
}

function locateAssetCard(ticketPage: Page, assetSymbol: string) {
  return ticketPage.getByTestId('exposicao-' + assetSymbol);
}

function measureRectangleOnScreen(elementOnScreen: Locator) {
  return elementOnScreen.boundingBox().then((elementRectangle) => {
    if (!elementRectangle) throw new Error('element has no box on screen');
    return elementRectangle;
  });
}

function calculateBadgeContrast(textColor: string, tintColor: string) {
  const readChannels = (rgbColor: string) => (rgbColor.match(/[\d.]+/g) ?? []).map(Number);
  const [cardRed, cardGreen, cardBlue] = readChannels(CARD_TOP_BACKGROUND_COLOR);
  const [tintRed, tintGreen, tintBlue, tintAlpha = 1] = readChannels(tintColor);
  const blendOverCard = (tintChannel: number, cardChannel: number) => tintAlpha * tintChannel + (1 - tintAlpha) * cardChannel;
  const badgeBackground = [blendOverCard(tintRed, cardRed), blendOverCard(tintGreen, cardGreen), blendOverCard(tintBlue, cardBlue)];
  const calculateChannelLuminance = (colorChannel: number) => {
    const normalizedChannel = colorChannel / 255;
    return normalizedChannel <= 0.03928 ? normalizedChannel / 12.92 : ((normalizedChannel + 0.055) / 1.055) ** 2.4;
  };
  const calculateColorLuminance = ([red, green, blue]: number[]) => 0.2126 * calculateChannelLuminance(red) + 0.7152 * calculateChannelLuminance(green) + 0.0722 * calculateChannelLuminance(blue);
  const [higherLuminance, lowerLuminance] = [calculateColorLuminance(readChannels(textColor)), calculateColorLuminance(badgeBackground)].sort((firstLuminance, secondLuminance) => secondLuminance - firstLuminance);
  return (higherLuminance + 0.05) / (lowerLuminance + 0.05);
}

async function checkSideBadge(assetCard: Locator, assetSymbol: string, expectedBadgeLabel: SideBadgeLabel) {
  const sideBadge = assetCard.getByTestId('exposicao-lado');
  await expect(sideBadge).toHaveCount(1);
  await expect(sideBadge).toBeVisible();
  await expect(sideBadge).toHaveText(expectedBadgeLabel);
  // A screen reader hears the side right after the asset name.
  await expect(assetCard.locator('.exposure-asset')).toMatchAriaSnapshot(`
    - heading "${assetSymbol}" [level=3]
    - text: ${expectedBadgeLabel}
  `);
  const expectedBadgeColors = EXPECTED_SIDE_BADGES[expectedBadgeLabel];
  await expect(sideBadge).toHaveCSS('color', expectedBadgeColors.textColor);
  await expect(sideBadge).toHaveCSS('background-color', expectedBadgeColors.backgroundColor);
  await expect(sideBadge).toHaveCSS('border-radius', '999px');
  // With the page at 90%, Chrome rounds the 1 px border to one whole screen pixel, so the computed CSS width reads 1.11 px.
  for (const borderSide of ['top', 'right', 'bottom', 'left']) {
    await expect(sideBadge).toHaveCSS(`border-${borderSide}-style`, 'solid');
    await expect(sideBadge).toHaveCSS(`border-${borderSide}-color`, expectedBadgeColors.borderColor);
    const borderWidthOnScreen = PAGE_SCALE * parseFloat(await sideBadge.evaluate((badgeOnPage, cssProperty) => getComputedStyle(badgeOnPage).getPropertyValue(cssProperty), `border-${borderSide}-width`));
    expect(borderWidthOnScreen, `${assetSymbol} badge border ${borderSide}`).toBeCloseTo(1, 1);
  }
  expect(calculateBadgeContrast(expectedBadgeColors.textColor, expectedBadgeColors.backgroundColor), `${assetSymbol} badge contrast`).toBeGreaterThanOrEqual(4.5);

  const symbolBox = await measureRectangleOnScreen(assetCard.getByRole('heading', { level: 3, name: assetSymbol }));
  const badgeBox = await measureRectangleOnScreen(sideBadge);
  const assetCardBox = await measureRectangleOnScreen(assetCard);
  expect(badgeBox.x).toBeGreaterThan(symbolBox.x + symbolBox.width);
  expect(badgeBox.x - (symbolBox.x + symbolBox.width)).toBeLessThan(16);
  expect(Math.abs(badgeBox.y + badgeBox.height / 2 - (symbolBox.y + symbolBox.height / 2))).toBeLessThanOrEqual(2);
  expect(badgeBox.x + badgeBox.width).toBeLessThanOrEqual(assetCardBox.x + assetCardBox.width);
}

async function checkAssetIcon(assetCard: Locator, isPointingDown: boolean) {
  const assetIcon = assetCard.locator('.exposure-icon');
  await expect(assetIcon.locator('svg')).toBeVisible();
  await expect(assetIcon).toHaveCSS('color', isPointingDown ? SELL_COLOR : ACCENT_COLOR);
  await expect(assetIcon.locator('svg')).toHaveCSS('transform', isPointingDown ? MIRRORED_DOWN_TRANSFORM : 'none');
}

async function checkRemainingFigures(
  assetCard: Locator,
  expectedFigures: { currentExposure: string; remainingToBuy: string; remainingToSell: string; remainingOnPositionSide: 'buy' | 'sell' },
) {
  await expect(assetCard.getByTestId('exposicao-atual')).toHaveText(expectedFigures.currentExposure);
  await expect(assetCard.locator('dt', { hasText: 'Falta para comprar' })).toHaveCount(1);
  await expect(assetCard.locator('dt', { hasText: 'Falta para vender' })).toHaveCount(1);
  await expect(assetCard.getByText('Falta até o limite')).toHaveCount(0);
  await expect(assetCard.getByTestId('falta-para-comprar')).toHaveText(expectedFigures.remainingToBuy);
  await expect(assetCard.getByTestId('falta-para-vender')).toHaveText(expectedFigures.remainingToSell);
  // The older E2E files read the figure that equals the API "remaining" through this test id.
  const figureOnPositionSide = assetCard.getByTestId(expectedFigures.remainingOnPositionSide === 'buy' ? 'falta-para-comprar' : 'falta-para-vender');
  await expect(assetCard.getByTestId('exposicao-restante')).toHaveCount(1);
  await expect(figureOnPositionSide.getByTestId('exposicao-restante')).toHaveText(
    expectedFigures.remainingOnPositionSide === 'buy' ? expectedFigures.remainingToBuy : expectedFigures.remainingToSell,
  );
}

test.beforeEach(async ({ page }) => {
  await deleteAllOrdersOnServer(page);
});

test.afterEach(async ({ page }) => {
  await deleteAllOrdersOnServer(page);
});

for (const windowWidth of [1920, 1440, 860, 375]) {
  test(`CA-5: at ${windowWidth} px each card shows Comprado, Vendido or Zerado, the down icon when sold and the two remaining figures`, async ({ page }) => {
    // PETR4 bought R$ 1.000,00; VALE3 sold exactly R$ 39.999.535,62 (the example checked in CA-5); VIIA4 untouched.
    await sendOrderThroughApi(page, 'PETR4', 'buy', 100, 10);
    await sendOrderThroughApi(page, 'VALE3', 'sell', 39_999, 999.99);
    await sendOrderThroughApi(page, 'VALE3', 'sell', 1, 935.61);
    expect((await readServerExposure(page, 'VALE3')).exposure).toBe(-39_999_535.62);

    await page.setViewportSize({ width: windowWidth, height: 900 });
    await page.goto('/');
    await expect(locateAssetCard(page, 'VIIA4')).toBeVisible();

    const boughtCard = locateAssetCard(page, 'PETR4');
    await checkSideBadge(boughtCard, 'PETR4', 'Comprado');
    await checkAssetIcon(boughtCard, false);
    await checkRemainingFigures(boughtCard, {
      currentExposure: 'R$ 1.000,00',
      remainingToBuy: 'R$ 99.999.000,00',
      remainingToSell: 'R$ 100.001.000,00',
      remainingOnPositionSide: 'buy',
    });

    const soldCard = locateAssetCard(page, 'VALE3');
    await checkSideBadge(soldCard, 'VALE3', 'Vendido');
    await checkAssetIcon(soldCard, true);
    await checkRemainingFigures(soldCard, {
      currentExposure: '-R$ 39.999.535,62',
      remainingToBuy: 'R$ 139.999.535,62',
      remainingToSell: 'R$ 60.000.464,38',
      remainingOnPositionSide: 'sell',
    });
    // The limit usage keeps the unsigned value (CA-6).
    await expect(soldCard.getByTestId('uso-do-limite-porcentagem')).toHaveText('39,99%');

    const flatCard = locateAssetCard(page, 'VIIA4');
    await checkSideBadge(flatCard, 'VIIA4', 'Zerado');
    await checkAssetIcon(flatCard, false);
    await checkRemainingFigures(flatCard, {
      currentExposure: 'R$ 0,00',
      remainingToBuy: 'R$ 100.000.000,00',
      remainingToSell: 'R$ 100.000.000,00',
      remainingOnPositionSide: 'buy',
    });

    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(
      await page.evaluate(() => document.documentElement.clientWidth),
    );
    await page.locator('section.exposure').screenshot({ path: path.join(EVIDENCE_FOLDER, `04-exposure-sides-${windowWidth}.png`) });
  });
}

// The widest values that fit the limit: PETR4 sold and VALE3 bought by 99,999 × 999.99, so one remaining figure reads
// R$ 199.998.000,01. Between 861 and 1080 px the three cards are narrow; the split must come from the card width alone.
const WIDEST_VALUE_CARDS = [
  { assetSymbol: 'PETR4', currentExposure: '-R$ 99.998.000,01', remainingToBuy: 'R$ 199.998.000,01', remainingToSell: 'R$ 1.999,99' },
  { assetSymbol: 'VALE3', currentExposure: 'R$ 99.998.000,01', remainingToBuy: 'R$ 1.999,99', remainingToSell: 'R$ 199.998.000,01' },
  { assetSymbol: 'VIIA4', currentExposure: 'R$ 0,00', remainingToBuy: 'R$ 100.000.000,00', remainingToSell: 'R$ 100.000.000,00' },
] as const;

for (const { windowWidth, remainingFiguresLayout, areCardsInOneRow } of [
  { windowWidth: 1920, remainingFiguresLayout: 'side-by-side', areCardsInOneRow: true },
  { windowWidth: 1440, remainingFiguresLayout: 'side-by-side', areCardsInOneRow: true },
  { windowWidth: 1080, remainingFiguresLayout: 'side-by-side', areCardsInOneRow: true },
  { windowWidth: 1024, remainingFiguresLayout: 'side-by-side', areCardsInOneRow: true },
  { windowWidth: 960, remainingFiguresLayout: 'stacked', areCardsInOneRow: true },
  { windowWidth: 900, remainingFiguresLayout: 'stacked', areCardsInOneRow: true },
  { windowWidth: 860, remainingFiguresLayout: 'side-by-side', areCardsInOneRow: false },
  { windowWidth: 375, remainingFiguresLayout: 'side-by-side', areCardsInOneRow: false },
] as const) {
  test(`RNF-02: at ${windowWidth} px, with the widest values, the remaining figures sit ${remainingFiguresLayout} in all three cards, each number on one line inside its card`, async ({ page }) => {
    await sendOrderThroughApi(page, 'PETR4', 'sell', 99_999, 999.99);
    await sendOrderThroughApi(page, 'VALE3', 'buy', 99_999, 999.99);
    await page.setViewportSize({ width: windowWidth, height: 900 });
    await page.goto('/');
    await expect(locateAssetCard(page, 'VIIA4')).toBeVisible();

    const limitUsageTrackTops: number[] = [];
    for (const expectedCard of WIDEST_VALUE_CARDS) {
      const assetCard = locateAssetCard(page, expectedCard.assetSymbol);
      await expect(assetCard.getByTestId('exposicao-atual')).toHaveText(expectedCard.currentExposure);
      await expect(assetCard.getByTestId('falta-para-comprar')).toHaveText(expectedCard.remainingToBuy);
      await expect(assetCard.getByTestId('falta-para-vender')).toHaveText(expectedCard.remainingToSell);
      const assetCardBox = await measureRectangleOnScreen(assetCard);
      for (const cardFigureTestId of ['exposicao-atual', 'falta-para-comprar', 'falta-para-vender']) {
        const cardFigure = assetCard.getByTestId(cardFigureTestId);
        const isFigureOnOneLine = await cardFigure.evaluate(
          (figureOnPage) => figureOnPage.getBoundingClientRect().height < parseFloat(getComputedStyle(figureOnPage).fontSize) * 2,
        );
        expect(isFigureOnOneLine, `${expectedCard.assetSymbol} / ${cardFigureTestId}`).toBe(true);
        const figureTextRightEdge = await cardFigure.evaluate((figureOnPage) => {
          const figureTextRange = document.createRange();
          figureTextRange.selectNodeContents(figureOnPage);
          return figureTextRange.getBoundingClientRect().right;
        });
        expect(figureTextRightEdge, `${expectedCard.assetSymbol} / ${cardFigureTestId} inside the card`).toBeLessThanOrEqual(assetCardBox.x + assetCardBox.width);
      }
      const remainingToBuyBox = await measureRectangleOnScreen(assetCard.getByTestId('falta-para-comprar'));
      const remainingToSellBox = await measureRectangleOnScreen(assetCard.getByTestId('falta-para-vender'));
      if (remainingFiguresLayout === 'side-by-side') {
        expect(Math.abs(remainingToSellBox.y - remainingToBuyBox.y), `${expectedCard.assetSymbol} same line`).toBeLessThanOrEqual(1);
        expect(remainingToSellBox.x).toBeGreaterThan(remainingToBuyBox.x + remainingToBuyBox.width);
      } else {
        expect(remainingToSellBox.y, `${expectedCard.assetSymbol} stacked`).toBeGreaterThan(remainingToBuyBox.y + remainingToBuyBox.height);
        expect(Math.abs(remainingToSellBox.x - remainingToBuyBox.x)).toBeLessThanOrEqual(1);
      }
      limitUsageTrackTops.push((await measureRectangleOnScreen(assetCard.getByRole('meter'))).y);
    }
    // Cards in the same row keep the same shape, so the three "Uso do limite" bars line up.
    if (areCardsInOneRow) {
      expect(Math.abs(limitUsageTrackTops[1] - limitUsageTrackTops[0])).toBeLessThanOrEqual(1);
      expect(Math.abs(limitUsageTrackTops[2] - limitUsageTrackTops[0])).toBeLessThanOrEqual(1);
    }
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(
      await page.evaluate(() => document.documentElement.clientWidth),
    );
    await page.locator('section.exposure').screenshot({ path: path.join(EVIDENCE_FOLDER, `04-exposure-widest-${windowWidth}.png`) });
  });
}

test('CA-6: selling down to near −100 mi, the next sell is rejected and a buy is accepted; the card follows each answer', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/');
  await expect(locateAssetCard(page, 'PETR4').getByTestId('exposicao-lado')).toHaveText('Zerado');

  // 99,999 × 999.99 = 99,998,000.01: a sell goes negative and the card turns to Vendido.
  expect(await sendOrderThroughOrderTicket(page, 'PETR4', 'Venda', '99999', '999,99')).toBe('accepted');
  const soldCard = locateAssetCard(page, 'PETR4');
  await expect(soldCard.getByTestId('exposicao-atual')).toHaveText('-R$ 99.998.000,01');
  await checkSideBadge(soldCard, 'PETR4', 'Vendido');
  await checkAssetIcon(soldCard, true);
  await checkRemainingFigures(soldCard, {
    currentExposure: '-R$ 99.998.000,01',
    remainingToBuy: 'R$ 199.998.000,01',
    remainingToSell: 'R$ 1.999,99',
    remainingOnPositionSide: 'sell',
  });
  await expect(soldCard.getByTestId('uso-do-limite-porcentagem')).toHaveText('99,99%');
  await expect(soldCard.getByTestId('uso-do-limite-preenchimento')).toHaveCSS('background-image', AMBER_BAR_GRADIENT);

  // 3 × 999.99 = R$ 2.999,97 is more than the R$ 1.999,99 left to sell: past −100,000,000 in absolute value, rejected, nothing changes.
  expect(await sendOrderThroughOrderTicket(page, 'PETR4', 'Venda', '3', '999,99')).toBe('rejected');
  expect(await sendOrderThroughOrderTicket(page, 'PETR4', 'Venda', '99999', '999,99')).toBe('rejected');
  expect((await readServerExposure(page, 'PETR4')).exposure).toBe(-99_998_000.01);
  await expect(soldCard.getByTestId('exposicao-atual')).toHaveText('-R$ 99.998.000,01');
  await expect(soldCard.getByTestId('falta-para-vender')).toHaveText('R$ 1.999,99');

  // A buy moves away from the limit on the sell side: accepted, and the card goes back to Zerado.
  expect(await sendOrderThroughOrderTicket(page, 'PETR4', 'Compra', '99999', '999,99')).toBe('accepted');
  expect((await readServerExposure(page, 'PETR4')).exposure).toBe(0);
  await checkSideBadge(soldCard, 'PETR4', 'Zerado');
  await checkAssetIcon(soldCard, false);
  await checkRemainingFigures(soldCard, {
    currentExposure: 'R$ 0,00',
    remainingToBuy: 'R$ 100.000.000,00',
    remainingToSell: 'R$ 100.000.000,00',
    remainingOnPositionSide: 'buy',
  });
  await expect(soldCard.getByTestId('uso-do-limite-porcentagem')).toHaveText('0%');
});
