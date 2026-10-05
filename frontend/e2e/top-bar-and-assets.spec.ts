import { readFileSync } from 'node:fs';
import { expect, test, type APIRequestContext, type Locator, type Page } from '@playwright/test';
import { CREATE_ORDER_ROUTE, EXPOSURES_ROUTE } from '../src/services/ordersService';

// Talks to the real OrderGenerator and OrderAccumulator. Each scenario that changes the exposure
// takes the symbol to an exact value through real orders and, at the end of the file, returns each symbol
// to the value it had before, so the starting point of the other files does not change.

const EXPECTED_DATADOG_DASHBOARDS = [
  {
    dashboardName: 'Four Golden Signals',
    dashboardUrl:
      'https://p.datadoghq.com/sb/63578a59-bd12-11f1-a546-261a98ac5284-a9e17306767d8f22537a7acb53350942?refresh_mode=sliding&tpl_var_ecs_service%5B0%5D=%2A&tpl_var_env%5B0%5D=dev&tpl_var_service%5B0%5D=%2A&tpl_var_version%5B0%5D=%2A&from_ts=1791086628736&to_ts=1791101028736&live=true',
  },
  {
    dashboardName: 'Jornada da ordem',
    dashboardUrl:
      'https://app.datadoghq.com/dashboard/mvw-rz6-i7h?fromUser=false&graphType=flamegraph&refresh_mode=sliding&shouldShowLegend=true&traceQuery=&from_ts=1791086615296&to_ts=1791101015296&live=true',
  },
  {
    dashboardName: 'Ordens e exposição',
    dashboardUrl:
      'https://p.datadoghq.com/sb/63578a59-bd12-11f1-a546-261a98ac5284-cb393cd3b3760676912c981fa71f3372?refresh_mode=sliding&tpl_var_env%5B0%5D=dev&tpl_var_service%5B0%5D=order-accumulator&tpl_var_side%5B0%5D=%2A&tpl_var_symbol%5B0%5D=%2A&from_ts=1791086654893&to_ts=1791101054893&live=true',
  },
] as const;

const ASSET_CARD_SYMBOLS = ['PETR4', 'VALE3', 'VIIA4'] as const;
const ACCENT_COLOR = 'rgb(79, 227, 176)';
const LABEL_COLOR = 'rgb(143, 163, 161)';
const BORDER_COLOR = 'rgb(28, 48, 52)';
const GREEN_BAR_GRADIENT = 'linear-gradient(90deg, rgb(47, 199, 149), rgb(79, 227, 176))';
const AMBER_BAR_GRADIENT = 'linear-gradient(90deg, rgb(233, 167, 60), rgb(244, 197, 106))';
const NEAR_LIMIT_PERCENTAGE_COLOR = 'rgb(244, 197, 106)';
const TEXT_COLOR = 'rgb(232, 239, 238)';
const LINK_BACKGROUND_COLOR = 'rgb(14, 27, 30)';
const LINK_BACKGROUND_COLOR_ON_HOVER = 'rgb(18, 36, 39)';
// The cards are 14 CSS px apart (mockup 01); on screen, with the page at 90%, that is 12.6 px.
const SPACE_BETWEEN_CARDS_ON_SCREEN = 14 * 0.9;
const MAX_ORDER_PRICE_IN_CENTS = 99_999;
const MAX_ORDER_QUANTITY = 99_999;

type ServerExposure = { symbol: string; exposure: number; remaining: number };

async function readServerExposureInCents(testRequest: APIRequestContext, exposureSymbol: string) {
  const exposuresResponse = await testRequest.get(EXPOSURES_ROUTE);
  expect(exposuresResponse.status()).toBe(200);
  const exposuresBody = ((await exposuresResponse.json()) as { data: { exposures: ServerExposure[] } }).data;
  const symbolExposure = exposuresBody.exposures.find((serverExposure) => serverExposure.symbol === exposureSymbol);
  if (!symbolExposure) throw new Error('Symbol ' + exposureSymbol + ' missing from /api/exposures');
  return Math.round(symbolExposure.exposure * 100);
}

async function sendAcceptedOrderThroughApi(testRequest: APIRequestContext, symbol: string, side: 'buy' | 'sell', quantity: number, priceInCents: number) {
  const orderResponse = await testRequest.post(CREATE_ORDER_ROUTE, {
    data: { symbol, side, quantity, price: priceInCents / 100 },
  });
  expect(orderResponse.status()).toBe(200);
  expect(((await orderResponse.json()) as { data: { status: string } }).data.status).toBe('accepted');
}

// Always moves toward the target, so no intermediate order goes past the limit.
async function moveSymbolExposureTo(testRequest: APIRequestContext, symbol: string, targetExposureInCents: number) {
  const differenceInCents = targetExposureInCents - (await readServerExposureInCents(testRequest, symbol));
  const ordersSide = differenceInCents >= 0 ? 'buy' : 'sell';
  let centsLeftToMove = Math.abs(differenceInCents);
  while (centsLeftToMove > 0) {
    const quantityAtMaxPrice = Math.min(Math.floor(centsLeftToMove / MAX_ORDER_PRICE_IN_CENTS), MAX_ORDER_QUANTITY);
    if (quantityAtMaxPrice > 0) {
      await sendAcceptedOrderThroughApi(testRequest, symbol, ordersSide, quantityAtMaxPrice, MAX_ORDER_PRICE_IN_CENTS);
      centsLeftToMove -= quantityAtMaxPrice * MAX_ORDER_PRICE_IN_CENTS;
    } else {
      await sendAcceptedOrderThroughApi(testRequest, symbol, ordersSide, 1, centsLeftToMove);
      centsLeftToMove = 0;
    }
  }
  expect(await readServerExposureInCents(testRequest, symbol)).toBe(targetExposureInCents);
}

function locateAssetCard(orderTicketPage: Page, assetSymbol: string) {
  return orderTicketPage.getByTestId('exposicao-' + assetSymbol);
}

async function checkAssetCard(
  orderTicketPage: Page,
  assetSymbol: string,
  // filledTrackFraction: filled fraction of the track; 'minimum-mark' = non-zero exposure that does not reach
  // 1 px of the track and shows as a dot the size of the bar height.
  expectedAssetCard: {
    currentExposure: string;
    remainingToLimit: string;
    limitUsage: string;
    meterValue: string;
    fillGradient: string;
    filledTrackFraction: number | 'minimum-mark';
  },
) {
  const assetCard = locateAssetCard(orderTicketPage, assetSymbol);
  await expect(assetCard.getByTestId('exposicao-atual')).toHaveText(expectedAssetCard.currentExposure);
  await expect(assetCard.getByTestId('exposicao-restante')).toHaveText(expectedAssetCard.remainingToLimit);
  await expect(assetCard.getByTestId('uso-do-limite-porcentagem')).toHaveText(expectedAssetCard.limitUsage);
  // The percentage follows the bar color: amber near the limit, normal text otherwise.
  await expect(assetCard.getByTestId('uso-do-limite-porcentagem')).toHaveCSS(
    'color',
    expectedAssetCard.fillGradient === AMBER_BAR_GRADIENT ? NEAR_LIMIT_PERCENTAGE_COLOR : TEXT_COLOR,
  );
  const limitUsageTrack = assetCard.getByRole('meter', { name: 'Uso do limite de ' + assetSymbol });
  await expect(limitUsageTrack).toHaveAttribute('aria-valuetext', expectedAssetCard.limitUsage);
  await expect(limitUsageTrack).toHaveAttribute('aria-valuenow', expectedAssetCard.meterValue);
  const limitUsageFill = assetCard.getByTestId('uso-do-limite-preenchimento');
  await expect(limitUsageFill).toHaveCSS('background-image', expectedAssetCard.fillGradient);
  const trackBox = await measureRectangleOnScreen(limitUsageTrack);
  const filledWidth = (await limitUsageFill.boundingBox())?.width ?? 0;
  if (expectedAssetCard.filledTrackFraction === 'minimum-mark') expect(filledWidth).toBeCloseTo(trackBox.height, 1);
  else expect(filledWidth / trackBox.width).toBeCloseTo(expectedAssetCard.filledTrackFraction, 2);
}

// With the page at 90%, Chrome rounds borders and outlines to the whole screen pixel, and the
// computed CSS size comes out with decimals (46px becomes 45.99px). Compares with a 0.05 px tolerance.
async function readCssSizeInPx(elementOnScreen: Locator, cssProperty: 'height' | 'width' | 'outline-width') {
  return parseFloat(await elementOnScreen.evaluate((elementOnPage, propertyName) => getComputedStyle(elementOnPage).getPropertyValue(propertyName), cssProperty));
}

async function readPageScale(orderTicketPage: Page) {
  return parseFloat(await orderTicketPage.evaluate(() => getComputedStyle(document.documentElement).zoom));
}

function measureRectangleOnScreen(elementOnScreen: Locator) {
  return elementOnScreen.boundingBox().then((elementRectangle) => {
    if (!elementRectangle) throw new Error('element has no box on screen');
    return elementRectangle;
  });
}

function rectanglesOverlap(
  firstRectangle: { x: number; y: number; width: number; height: number },
  secondRectangle: { x: number; y: number; width: number; height: number },
) {
  return (
    firstRectangle.x < secondRectangle.x + secondRectangle.width &&
    secondRectangle.x < firstRectangle.x + firstRectangle.width &&
    firstRectangle.y < secondRectangle.y + secondRectangle.height &&
    secondRectangle.y < firstRectangle.y + firstRectangle.height
  );
}

// Text baseline, on screen: an empty inline mark sits exactly on it.
function measureBaselineOnScreen(textOnScreen: Locator) {
  return textOnScreen.evaluate((elementWithText) => {
    const baselineMark = document.createElement('span');
    baselineMark.style.display = 'inline-block';
    baselineMark.style.height = '0';
    elementWithText.append(baselineMark);
    const baselineTop = baselineMark.getBoundingClientRect().top;
    baselineMark.remove();
    return baselineTop;
  });
}

// The expected drawing of each icon comes from Icons.tsx itself: the test proves the icon on screen is that one, not any SVG.
function readIconPathsFromSource(iconName: string) {
  const iconsSource = readFileSync(new URL('../src/components/Icons.tsx', import.meta.url), 'utf8');
  const iconStart = iconsSource.indexOf(`export function ${iconName}(`);
  if (iconStart === -1) throw new Error(`${iconName} does not exist in Icons.tsx`);
  const iconEnd = iconsSource.indexOf('export function', iconStart + 1);
  const iconSource = iconsSource.slice(iconStart, iconEnd === -1 ? undefined : iconEnd);
  return [...iconSource.matchAll(/ d="([^"]+)"/g)].map((pathMatch) => pathMatch[1]);
}

async function checkIconDrawing(iconOnScreen: Locator, iconName: string) {
  const expectedPaths = readIconPathsFromSource(iconName);
  expect(expectedPaths.length).toBeGreaterThan(0);
  expect(await iconOnScreen.locator('path').evaluateAll((svgPaths) => svgPaths.map((svgPath) => svgPath.getAttribute('d')))).toEqual(expectedPaths);
}

// WCAG contrast between two "rgb(r, g, b)" colors.
function calculateContrast(textColor: string, backgroundColor: string) {
  const colorLuminance = (rgbColor: string) => {
    const [red, green, blue] = (rgbColor.match(/\d+/g) ?? []).slice(0, 3).map((colorChannel) => {
      const normalizedChannel = Number(colorChannel) / 255;
      return normalizedChannel <= 0.03928 ? normalizedChannel / 12.92 : ((normalizedChannel + 0.055) / 1.055) ** 2.4;
    });
    return 0.2126 * red + 0.7152 * green + 0.0722 * blue;
  };
  const [higherLuminance, lowerLuminance] = [colorLuminance(textColor), colorLuminance(backgroundColor)].sort((firstLuminance, secondLuminance) => secondLuminance - firstLuminance);
  return (higherLuminance + 0.05) / (lowerLuminance + 0.05);
}

// Full thin border: all four sides, not just the top one, in the mockup color and line style.
async function checkThinBorderOnAllFourSides(elementWithBorder: Locator) {
  for (const borderSide of ['top', 'right', 'bottom', 'left']) {
    await expect(elementWithBorder).toHaveCSS(`border-${borderSide}-style`, 'solid');
    await expect(elementWithBorder).toHaveCSS(`border-${borderSide}-color`, BORDER_COLOR);
  }
}

async function readComputedColor(elementOnScreen: Locator, colorProperty: 'color' | 'background-color') {
  return elementOnScreen.evaluate((elementOnPage, propertyName) => getComputedStyle(elementOnPage).getPropertyValue(propertyName), colorProperty);
}

const exposuresBeforeFileInCents = new Map<string, number>();

test.beforeAll(async ({ request }) => {
  for (const assetSymbol of ASSET_CARD_SYMBOLS) {
    exposuresBeforeFileInCents.set(assetSymbol, await readServerExposureInCents(request, assetSymbol));
  }
});

test.afterAll(async ({ request }) => {
  for (const [assetSymbol, exposureBeforeInCents] of exposuresBeforeFileInCents) {
    await moveSymbolExposureTo(request, assetSymbol, exposureBeforeInCents);
  }
});

for (const windowWidth of [1440, 1920]) {
  test(`CA-9: at ${windowWidth} px the 3 Datadog links sit in the top bar, in this order, with logo, label, name and arrow, and open in a new tab`, async ({ page }) => {
    await page.setViewportSize({ width: windowWidth, height: 900 });
    await page.goto('/');
    const topBar = page.getByRole('banner');
    await expect(topBar.getByRole('link')).toHaveCount(3);

    let previousLinkRightEdge = 0;
    for (const expectedDashboard of EXPECTED_DATADOG_DASHBOARDS) {
      const dashboardLink = topBar.getByRole('link', { name: new RegExp(expectedDashboard.dashboardName) });
      await expect(dashboardLink).toHaveCount(1);
      await expect(dashboardLink).toBeVisible();
      expect(await dashboardLink.getAttribute('href')).toBe(expectedDashboard.dashboardUrl);
      await expect(dashboardLink).toHaveAttribute('target', '_blank');
      await expect(dashboardLink).toHaveAttribute('rel', 'noopener noreferrer');
      // Screen reader users hear the brand, the dashboard and the notice that it opens another tab.
      await expect(dashboardLink).toHaveAccessibleName(`Datadog ${expectedDashboard.dashboardName} (abre em nova aba)`);
      await expect(dashboardLink).toHaveCSS('border-radius', '14px');
      expect(await readCssSizeInPx(dashboardLink, 'height')).toBeCloseTo(46, 1);
      await expect(dashboardLink).toHaveCSS('background-color', LINK_BACKGROUND_COLOR);
      await checkThinBorderOnAllFourSides(dashboardLink);

      const dashboardNameText = dashboardLink.locator('.datadog-dashboard-name');
      await expect(dashboardNameText).toHaveText(expectedDashboard.dashboardName);
      await expect(dashboardNameText).toHaveCSS('font-size', '13px');
      await expect(dashboardNameText).toHaveCSS('font-weight', '800');
      await expect(dashboardNameText).toHaveCSS('color', TEXT_COLOR);
      expect(calculateContrast(await readComputedColor(dashboardNameText, 'color'), LINK_BACKGROUND_COLOR)).toBeGreaterThanOrEqual(4.5);

      const dashboardBrand = dashboardLink.locator('.datadog-dashboard-brand');
      await expect(dashboardBrand).toHaveText('DATADOG', { useInnerText: true });
      await expect(dashboardBrand).toHaveCSS('font-size', '10px');
      await expect(dashboardBrand).toHaveCSS('color', LABEL_COLOR);
      expect(calculateContrast(await readComputedColor(dashboardBrand, 'color'), LINK_BACKGROUND_COLOR)).toBeGreaterThanOrEqual(4.5);

      const dashboardLogo = dashboardLink.locator('.datadog-dashboard-logo');
      await expect(dashboardLogo).toHaveCSS('background-color', 'rgb(255, 255, 255)');
      expect(await readCssSizeInPx(dashboardLogo, 'width')).toBeCloseTo(28, 1);
      expect(await readCssSizeInPx(dashboardLogo, 'height')).toBeCloseTo(28, 1);
      await expect(dashboardLogo.locator('svg')).toBeVisible();
      await checkIconDrawing(dashboardLogo.locator('svg'), 'DatadogLogo');
      await expect(dashboardLink.locator('svg.datadog-dashboard-arrow')).toBeVisible();
      await checkIconDrawing(dashboardLink.locator('svg.datadog-dashboard-arrow'), 'ExternalArrowIcon');

      const linkBox = await measureRectangleOnScreen(dashboardLink);
      expect(linkBox.x).toBeGreaterThanOrEqual(previousLinkRightEdge);
      previousLinkRightEdge = linkBox.x + linkBox.width;

      // On hover the whole border turns green and the background lightens; on leaving, everything goes back.
      await dashboardLink.hover();
      for (const borderSide of ['top', 'right', 'bottom', 'left']) await expect(dashboardLink).toHaveCSS(`border-${borderSide}-color`, ACCENT_COLOR);
      await expect(dashboardLink).toHaveCSS('background-color', LINK_BACKGROUND_COLOR_ON_HOVER);
      await page.mouse.move(0, 0);
      for (const borderSide of ['top', 'right', 'bottom', 'left']) await expect(dashboardLink).toHaveCSS(`border-${borderSide}-color`, BORDER_COLOR);
      await expect(dashboardLink).toHaveCSS('background-color', LINK_BACKGROUND_COLOR);
    }

    const logoBox = await measureRectangleOnScreen(page.getByRole('img', { name: 'Base investimentos' }));
    const firstLinkBox = await measureRectangleOnScreen(topBar.getByRole('link', { name: /Four Golden Signals/ }));
    expect(firstLinkBox.x).toBeGreaterThan(logoBox.x + logoBox.width);
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(
      await page.evaluate(() => document.documentElement.clientWidth),
    );
  });
}

test('CA-9: clicking each Datadog link opens its dashboard in another tab', async ({ page, context }) => {
  await page.goto('/');
  const orderTicketUrl = page.url();
  // The new tab never actually loads Datadog: the test only checks which URL it tried to open.
  await context.route(/datadoghq\.com/, (datadogRoute) => datadogRoute.fulfill({ status: 200, body: 'dashboard' }));
  for (const expectedDashboard of EXPECTED_DATADOG_DASHBOARDS) {
    const newTabEvent = context.waitForEvent('page');
    await page.getByRole('banner').getByRole('link', { name: new RegExp(expectedDashboard.dashboardName) }).click();
    const dashboardTab = await newTabEvent;
    await dashboardTab.waitForLoadState();
    expect(dashboardTab.url()).toBe(expectedDashboard.dashboardUrl);
    expect(page.url()).toBe(orderTicketUrl);
    await dashboardTab.close();
  }
});

for (const windowWidth of [1440, 1920]) {
  test(`CA-10: at ${windowWidth} px, to the right of the links sits the badge with a green shield, "AMBIENTE" and "Demonstração"`, async ({ page }) => {
    await page.setViewportSize({ width: windowWidth, height: 900 });
    await page.goto('/');
    const environmentBadge = page.getByRole('banner').locator('.environment-badge');
    await expect(environmentBadge).toHaveCount(1);
    await expect(environmentBadge.locator('.environment-badge-label')).toHaveText('AMBIENTE', { useInnerText: true });
    await expect(environmentBadge.locator('.environment-badge-name')).toHaveText('Demonstração');
    await expect(environmentBadge.locator('.environment-badge-icon')).toHaveCSS('background-color', 'rgba(79, 227, 176, 0.13)');
    await expect(environmentBadge.locator('.environment-badge-icon svg')).toHaveCSS('color', ACCENT_COLOR);
    await checkIconDrawing(environmentBadge.locator('.environment-badge-icon svg'), 'ShieldIcon');
    // "Demonstração" sits below "AMBIENTE", starting in the same column.
    const environmentLabelBox = await measureRectangleOnScreen(environmentBadge.locator('.environment-badge-label'));
    const environmentNameBox = await measureRectangleOnScreen(environmentBadge.locator('.environment-badge-name'));
    expect(environmentNameBox.y).toBeGreaterThanOrEqual(environmentLabelBox.y + environmentLabelBox.height);
    expect(environmentNameBox.x).toBeCloseTo(environmentLabelBox.x, 0);
    const environmentBadgeBox = await measureRectangleOnScreen(environmentBadge);
    const lastLinkBox = await measureRectangleOnScreen(page.getByRole('banner').getByRole('link', { name: /Ordens e exposição/ }));
    expect(environmentBadgeBox.x).toBeGreaterThanOrEqual(lastLinkBox.x + lastLinkBox.width);
    expect(Math.abs(environmentBadgeBox.y + environmentBadgeBox.height / 2 - (lastLinkBox.y + lastLinkBox.height / 2))).toBeLessThanOrEqual(1);
  });
}

test('CA-43: opening the screen fetches nothing from Datadog and no logo image file', async ({ page }) => {
  const requestedUrls: string[] = [];
  const requestedImages: string[] = [];
  page.on('request', (screenRequest) => {
    requestedUrls.push(screenRequest.url());
    if (screenRequest.resourceType() === 'image') requestedImages.push(screenRequest.url());
  });
  await page.goto('/', { waitUntil: 'networkidle' });
  await expect(page.getByRole('banner').getByRole('link')).toHaveCount(3);
  await expect(page.getByRole('banner').locator('.datadog-dashboard-logo svg')).toHaveCount(3);
  expect(requestedUrls.length).toBeGreaterThan(0);
  expect(requestedUrls.filter((requestedUrl) => /datadog/i.test(requestedUrl))).toEqual([]);
  expect(requestedImages).toEqual([]);
  const screenOrigin = new URL(page.url()).origin;
  expect(requestedUrls.filter((requestedUrl) => new URL(requestedUrl).origin !== screenOrigin)).toEqual([]);
});

for (const windowWidth of [1440, 1920]) {
  test(`CA-5: at ${windowWidth} px "Exposição por ativo" sits above everything, with the limit on the right and 3 cards side by side`, async ({ page }) => {
    await page.setViewportSize({ width: windowWidth, height: 900 });
    await page.goto('/');
    const exposureSection = page.locator('section.exposure');
    const exposureTitle = exposureSection.getByRole('heading', { level: 2, name: 'Exposição por ativo' });
    await expect(exposureTitle).toHaveCSS('font-family', /^Sora/);
    await expect(exposureTitle).toHaveCSS('font-size', '18px');
    const limitText = exposureSection.getByText('Limite por ativo · R$ 100.000.000,00');
    await expect(limitText).toBeVisible();
    await expect(limitText).toHaveCSS('font-size', '13px');
    await expect(limitText).toHaveCSS('color', LABEL_COLOR);
    const titleBox = await measureRectangleOnScreen(exposureTitle);
    const limitBox = await measureRectangleOnScreen(limitText);
    expect(limitBox.x).toBeGreaterThan(titleBox.x + titleBox.width);
    expect(Math.abs(limitBox.y + limitBox.height / 2 - (titleBox.y + titleBox.height / 2))).toBeLessThan(titleBox.height);

    // Above everything: no other grid block starts before the exposure section ends.
    const blocksBelowExposure = await page.locator('main').evaluate((layoutGrid) => {
      const exposureBottom = layoutGrid.querySelector('section.exposure')!.getBoundingClientRect().bottom;
      return [...layoutGrid.children].filter((gridBlock) => !gridBlock.classList.contains('exposure')).map((gridBlock) => gridBlock.getBoundingClientRect().top >= exposureBottom);
    });
    expect(blocksBelowExposure.length).toBeGreaterThan(0);
    expect(blocksBelowExposure.every((isBlockBelow) => isBlockBelow)).toBe(true);

    let previousCardRightEdge = 0;
    let firstCardTop: number | undefined;
    for (const assetSymbol of ASSET_CARD_SYMBOLS) {
      const assetCard = locateAssetCard(page, assetSymbol);
      await expect(assetCard).toHaveCount(1);
      await expect(assetCard).toBeVisible();
      await expect(assetCard.getByRole('heading', { level: 3 })).toHaveText(assetSymbol);
      await expect(assetCard.locator('.exposure-icon svg')).toBeVisible();
      await checkIconDrawing(assetCard.locator('.exposure-icon svg'), 'RisingChartIcon');
      expect(await readCssSizeInPx(assetCard.locator('.exposure-icon'), 'width')).toBeCloseTo(34, 1);
      expect(await readCssSizeInPx(assetCard.locator('.exposure-icon'), 'height')).toBeCloseTo(34, 1);
      await expect(assetCard.locator('dt', { hasText: 'Exposição atual' })).toHaveCount(1);
      await expect(assetCard.locator('dt', { hasText: 'Falta até o limite' })).toHaveCount(1);
      await expect(assetCard.getByTestId('exposicao-atual')).toHaveCSS('font-family', /^Sora/);
      await expect(assetCard.getByTestId('exposicao-atual')).toHaveCSS('font-size', '20px');
      await expect(assetCard).toHaveCSS('border-radius', '20px');
      await checkThinBorderOnAllFourSides(assetCard);
      await expect(assetCard).toHaveCSS('background-image', 'linear-gradient(rgb(15, 31, 34), rgb(14, 27, 30))');
      await expect(assetCard).toHaveCSS('box-shadow', 'rgba(0, 0, 0, 0.35) 0px 24px 50px 0px');
      const limitUsageCaption = assetCard.getByText('Uso do limite', { exact: true });
      const limitUsagePercentage = assetCard.getByTestId('uso-do-limite-porcentagem');
      await expect(limitUsageCaption).toHaveCSS('font-size', '12px');
      await expect(limitUsagePercentage).toHaveCSS('font-size', '12px');
      // The percentage sits to the right of the caption, on the same line.
      const limitUsageCaptionBox = await measureRectangleOnScreen(limitUsageCaption);
      const limitUsagePercentageBox = await measureRectangleOnScreen(limitUsagePercentage);
      expect(limitUsagePercentageBox.x).toBeGreaterThan(limitUsageCaptionBox.x + limitUsageCaptionBox.width);
      expect(Math.abs(limitUsagePercentageBox.y - limitUsageCaptionBox.y)).toBeLessThanOrEqual(1);
      expect(await readCssSizeInPx(assetCard.getByRole('meter'), 'height')).toBeCloseTo(6, 1);

      const assetCardBox = await measureRectangleOnScreen(assetCard);
      if (assetSymbol !== ASSET_CARD_SYMBOLS[0]) expect(assetCardBox.x).toBeCloseTo(previousCardRightEdge + SPACE_BETWEEN_CARDS_ON_SCREEN, 1);
      previousCardRightEdge = assetCardBox.x + assetCardBox.width;
      firstCardTop ??= assetCardBox.y;
      expect(assetCardBox.y).toBeCloseTo(firstCardTop, 0);

      const currentExposureBox = await measureRectangleOnScreen(assetCard.getByTestId('exposicao-atual'));
      const remainingToLimitBox = await measureRectangleOnScreen(assetCard.getByTestId('exposicao-restante'));
      expect(remainingToLimitBox.x).toBeGreaterThan(currentExposureBox.x);
      // As in the mockup: both labels start at the same height and the digits of both values sit on the same line.
      const exposureLabelBox = await measureRectangleOnScreen(assetCard.locator('dt', { hasText: 'Exposição atual' }));
      const remainingLabelBox = await measureRectangleOnScreen(assetCard.locator('dt', { hasText: 'Falta até o limite' }));
      expect(Math.abs(remainingLabelBox.y - exposureLabelBox.y)).toBeLessThanOrEqual(0.5);
      const currentExposureBaseline = await measureBaselineOnScreen(assetCard.getByTestId('exposicao-atual'));
      const remainingToLimitBaseline = await measureBaselineOnScreen(assetCard.getByTestId('exposicao-restante'));
      expect(Math.abs(remainingToLimitBaseline - currentExposureBaseline)).toBeLessThanOrEqual(1);
    }

    // The limit text ends at the same right edge as the last card, as in the mockup.
    const lastAssetCardBox = await measureRectangleOnScreen(locateAssetCard(page, 'VIIA4'));
    expect(limitBox.x + limitBox.width).toBeCloseTo(lastAssetCardBox.x + lastAssetCardBox.width, 0);
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(
      await page.evaluate(() => document.documentElement.clientWidth),
    );
  });
}

test('CA-6 and CA-39: the limit usage comes from the server, is truncated without rounding, turns amber from 90% and uses the unsigned value', async ({ page, request }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await moveSymbolExposureTo(request, 'VIIA4', 9_500_000_000);
  await moveSymbolExposureTo(request, 'VALE3', 0);
  await moveSymbolExposureTo(request, 'PETR4', -9_500_000_000);
  await page.goto('/');

  await checkAssetCard(page, 'VIIA4', {
    currentExposure: 'R$ 95.000.000,00',
    remainingToLimit: 'R$ 5.000.000,00',
    limitUsage: '95%',
    fillGradient: AMBER_BAR_GRADIENT,
    meterValue: '95',
    filledTrackFraction: 0.95,
  });
  await checkAssetCard(page, 'VALE3', {
    currentExposure: 'R$ 0,00',
    remainingToLimit: 'R$ 100.000.000,00',
    limitUsage: '0%',
    fillGradient: GREEN_BAR_GRADIENT,
    meterValue: '0',
    filledTrackFraction: 0,
  });
  await checkAssetCard(page, 'PETR4', {
    currentExposure: '-R$ 95.000.000,00',
    remainingToLimit: 'R$ 5.000.000,00',
    limitUsage: '95%',
    fillGradient: AMBER_BAR_GRADIENT,
    meterValue: '95',
    filledTrackFraction: 0.95,
  });

  // Buy through the order ticket itself: the screen rereads the exposure and the card changes with it, with no client-side math.
  // −95,000,000.00 + 99,999 × 950.00 = −950.00, which is more than zero and less than 0.01% of the limit.
  await expect(page.getByLabel('Quantidade de PETR4')).toBeVisible();
  await page.getByLabel('Quantidade de PETR4').fill('99999');
  await page.getByLabel('Preço por ação (R$)').fill('950,00');
  const exposureReadAfterSending = page.waitForResponse(
    (httpResponse) => httpResponse.request().method() === 'GET' && new URL(httpResponse.url()).pathname === EXPOSURES_ROUTE,
  );
  await page.getByRole('button', { name: 'Enviar ordem de compra' }).click();
  await exposureReadAfterSending;
  await checkAssetCard(page, 'PETR4', {
    currentExposure: '-R$ 950,00',
    remainingToLimit: 'R$ 99.999.050,00',
    limitUsage: '< 0,01%',
    fillGradient: GREEN_BAR_GRADIENT,
    meterValue: '0',
    filledTrackFraction: 'minimum-mark',
  });
  expect(await readServerExposureInCents(request, 'PETR4')).toBe(-95_000);
});

test('CA-6: on screen, "89,99%" stays green and 99.997…% shows as "99,99%" in amber, without rounding to 100%', async ({ page, request }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  // Values where truncating and rounding give different results, one on each side of the 90% threshold.
  await moveSymbolExposureTo(request, 'VIIA4', 8_999_999_999);
  await moveSymbolExposureTo(request, 'VALE3', 9_999_799_701);
  await page.goto('/');

  await checkAssetCard(page, 'VIIA4', {
    currentExposure: 'R$ 89.999.999,99',
    remainingToLimit: 'R$ 10.000.000,01',
    limitUsage: '89,99%',
    fillGradient: GREEN_BAR_GRADIENT,
    meterValue: '89.99',
    filledTrackFraction: 0.8999,
  });
  await checkAssetCard(page, 'VALE3', {
    currentExposure: 'R$ 99.997.997,01',
    remainingToLimit: 'R$ 2.002,99',
    limitUsage: '99,99%',
    fillGradient: AMBER_BAR_GRADIENT,
    meterValue: '99.99',
    filledTrackFraction: 0.9999,
  });
});

for (const windowWidth of [860, 375]) {
  test(`CA-26: at ${windowWidth} px the cards stack one below the other and the links and badge wrap without covering the logo`, async ({ page, request }) => {
    // The longest numbers that fit within the limit, with and without sign.
    await moveSymbolExposureTo(request, 'PETR4', -9_999_799_701);
    await moveSymbolExposureTo(request, 'VIIA4', 9_999_799_701);
    await page.setViewportSize({ width: windowWidth, height: 900 });
    await page.goto('/');
    await expect(locateAssetCard(page, 'VIIA4')).toBeVisible();

    let previousCardBottom = 0;
    let firstCardLeft: number | undefined;
    for (const assetSymbol of ASSET_CARD_SYMBOLS) {
      const assetCardBox = await measureRectangleOnScreen(locateAssetCard(page, assetSymbol));
      if (assetSymbol !== ASSET_CARD_SYMBOLS[0]) expect(assetCardBox.y).toBeCloseTo(previousCardBottom + SPACE_BETWEEN_CARDS_ON_SCREEN, 1);
      previousCardBottom = assetCardBox.y + assetCardBox.height;
      firstCardLeft ??= assetCardBox.x;
      expect(assetCardBox.x).toBeCloseTo(firstCardLeft, 0);
      // No value breaks in the middle of the number: each one takes a single line and fits in the card.
      for (const cardFigureTestId of ['exposicao-atual', 'exposicao-restante']) {
        const cardFigure = locateAssetCard(page, assetSymbol).getByTestId(cardFigureTestId);
        const isFigureOnOneLine = await cardFigure.evaluate(
          (figureOnPage) => figureOnPage.getBoundingClientRect().height < parseFloat(getComputedStyle(figureOnPage).fontSize) * 2,
        );
        expect(isFigureOnOneLine, `${assetSymbol} / ${cardFigureTestId}`).toBe(true);
        const cardFigureBox = await measureRectangleOnScreen(cardFigure);
        expect(cardFigureBox.x + cardFigureBox.width).toBeLessThanOrEqual(assetCardBox.x + assetCardBox.width);
      }
    }

    const logoBox = await measureRectangleOnScreen(page.getByRole('img', { name: 'Base investimentos' }));
    const visibleWidth = await page.evaluate(() => document.documentElement.clientWidth);
    const topBarItems = [
      ...EXPECTED_DATADOG_DASHBOARDS.map((expectedDashboard) => page.getByRole('banner').getByRole('link', { name: new RegExp(expectedDashboard.dashboardName) })),
      page.getByRole('banner').locator('.environment-badge'),
    ];
    for (const topBarItem of topBarItems) {
      await expect(topBarItem).toBeVisible();
      const topBarItemBox = await measureRectangleOnScreen(topBarItem);
      expect(rectanglesOverlap(topBarItemBox, logoBox)).toBe(false);
      expect(topBarItemBox.x).toBeGreaterThanOrEqual(0);
      expect(topBarItemBox.x + topBarItemBox.width).toBeLessThanOrEqual(visibleWidth);
    }
    for (const [topBarItemIndex, topBarItem] of topBarItems.entries()) {
      for (const otherTopBarItem of topBarItems.slice(topBarItemIndex + 1)) {
        expect(rectanglesOverlap(await measureRectangleOnScreen(topBarItem), await measureRectangleOnScreen(otherTopBarItem))).toBe(false);
      }
    }

    const contentWidth = await page.evaluate(() => document.documentElement.scrollWidth);
    expect(contentWidth).toBeLessThanOrEqual(visibleWidth);
  });
}

for (const windowWidth of [1440, 1920]) {
  test(`CA-27: at ${windowWidth} px each Datadog link receives keyboard focus with a visible outline`, async ({ page }) => {
    await page.setViewportSize({ width: windowWidth, height: 900 });
    await page.goto('/');
    for (const expectedDashboard of EXPECTED_DATADOG_DASHBOARDS) {
      const dashboardLink = page.getByRole('banner').getByRole('link', { name: new RegExp(expectedDashboard.dashboardName) });
      await page.keyboard.press('Tab');
      await expect(dashboardLink).toBeFocused();
      await expect(dashboardLink).toHaveCSS('outline-style', 'solid');
      // The outline must show at least 2 px on screen, already at 90%.
      const outlineOnScreenInPx = (await readCssSizeInPx(dashboardLink, 'outline-width')) * (await readPageScale(page));
      expect(Math.round(outlineOnScreenInPx * 100) / 100).toBeGreaterThanOrEqual(2);
      await expect(dashboardLink).toHaveCSS('outline-color', ACCENT_COLOR);
    }
  });
}
