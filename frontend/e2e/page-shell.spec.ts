import { expect, test, type Locator, type Page } from '@playwright/test';

// The page shell (F3): 90% size, content up to 1760 CSS px with 40 px margins,
// background with glow and grid, top bar with the logo and the line, and the bottom row in two columns.
// getBoundingClientRect returns the size ON SCREEN (already at 90%); CSS px = screen ÷ 0.9.
const PAGE_SCALE = 0.9;
const CONTENT_MAX_WIDTH_IN_CSS = 1760;
const SIDE_MARGIN_IN_CSS = 40;
const NEW_ORDER_WIDTH_IN_CSS = 400;
const TOP_BAR_LINE_COLOR = 'rgb(23, 42, 45)';
const SUBTITLE_TEXT =
  'Envie ordens de compra e venda de PETR4, VALE3 e VIIA4 e acompanhe a exposição de cada ativo até o limite de R$ 100.000.000,00.';

async function openOrderTicketAtWidth(orderTicketPage: Page, windowWidth: number) {
  await orderTicketPage.setViewportSize({ width: windowWidth, height: 1000 });
  await orderTicketPage.goto('/');
  await expect(orderTicketPage.getByTestId('exposicao-PETR4')).toBeVisible();
}

async function measureElementBoxOnScreen(pageElement: Locator) {
  return pageElement.evaluate((elementOnPage) => {
    const boxOnScreen = elementOnPage.getBoundingClientRect();
    return { left: boxOnScreen.left, right: boxOnScreen.right, top: boxOnScreen.top, bottom: boxOnScreen.bottom, width: boxOnScreen.width };
  });
}

test('CA-1: at the top there is the bar with the logo on the left and the thin line below it; underneath, the current title and subtitle', async ({ page }) => {
  await openOrderTicketAtWidth(page, 1440);
  const topBar = page.getByRole('banner');
  await expect(topBar.getByRole('img', { name: 'Base investimentos' })).toBeVisible();
  // At 90%, Chrome stores the 1 px screen line as 1.111 CSS px; what counts is the screen.
  const lineThicknessOnScreen = await topBar.evaluate(
    (topBarOnPage) => parseFloat(getComputedStyle(topBarOnPage).borderBottomWidth) * parseFloat(getComputedStyle(document.documentElement).zoom),
  );
  expect(lineThicknessOnScreen).toBeCloseTo(1, 3);
  await expect(topBar).toHaveCSS('border-bottom-style', 'solid');
  await expect(topBar).toHaveCSS('border-bottom-color', TOP_BAR_LINE_COLOR);
  await expect(page.getByRole('navigation')).toHaveCount(0);
  // The Datadog links go in the top bar (F4); the title and the subtitle have no link.
  await expect(page.locator('.page-header').getByRole('link')).toHaveCount(0);

  const topBarBox = await measureElementBoxOnScreen(topBar);
  const logoBox = await measureElementBoxOnScreen(topBar.getByRole('img', { name: 'Base investimentos' }));
  expect(logoBox.left).toBeCloseTo(topBarBox.left, 0);

  const pageTitle = page.getByRole('heading', { level: 1 });
  await expect(pageTitle).toHaveText('Boleta de ordens');
  await expect(pageTitle).toHaveCSS('font-family', /^Sora/);
  await expect(pageTitle).toHaveCSS('font-size', '34px');
  await expect(pageTitle).toHaveCSS('font-weight', '600');
  await expect(pageTitle).toHaveCSS('letter-spacing', '-1.02px');
  expect((await measureElementBoxOnScreen(pageTitle)).top).toBeGreaterThan(topBarBox.bottom);
  await expect(page.locator('.page-subtitle')).toHaveText(SUBTITLE_TEXT);
});

test('CA-2: with the browser at 100%, the page shows at 90% and a 15 px text measures 13.5 px on screen', async ({ page }) => {
  await openOrderTicketAtWidth(page, 1440);
  expect(await page.evaluate(() => window.devicePixelRatio)).toBe(1);
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).zoom)).toBe(String(PAGE_SCALE));

  const subtitleText = page.locator('.page-subtitle');
  await expect(subtitleText).toHaveCSS('font-size', '15px');
  // Letter on screen = CSS size × effective zoom of the paragraph itself, which Chrome reports in currentCSSZoom.
  const subtitleLetterOnScreen = await subtitleText.evaluate(
    (subtitleOnPage) => parseFloat(getComputedStyle(subtitleOnPage).fontSize) * (subtitleOnPage as HTMLElement & { currentCSSZoom: number }).currentCSSZoom,
  );
  expect(subtitleLetterOnScreen).toBeCloseTo(13.5, 3);
  // The paragraph line is 1.6 × 15 = 24 CSS px; on screen, each line measures 21.6 px.
  const singleLineHeightOnScreen = await subtitleText.evaluate((subtitleOnPage) => {
    const wholeTextRange = document.createRange();
    wholeTextRange.selectNodeContents(subtitleOnPage);
    const lineCount = new Set([...wholeTextRange.getClientRects()].map((lineFragment) => Math.round(lineFragment.top))).size;
    return subtitleOnPage.getBoundingClientRect().height / lineCount;
  });
  expect(singleLineHeightOnScreen).toBeCloseTo(24 * PAGE_SCALE, 0);
  // The order ticket is 400 CSS px; on screen, the same scale applies to the boxes.
  const orderTicketWidthOnScreen = (await measureElementBoxOnScreen(page.getByRole('form', { name: 'Boleta de ordem' }))).width;
  expect(orderTicketWidthOnScreen / NEW_ORDER_WIDTH_IN_CSS).toBeCloseTo(PAGE_SCALE, 3);
  await page.screenshot({ path: test.info().outputPath('ca-2-page-at-90.png') });
});

for (const windowWidth of [1440, 1920]) {
  test(`CA-3: at ${windowWidth} px the content uses up to 1760 CSS px, with 40 CSS px margins`, async ({ page }) => {
    await openOrderTicketAtWidth(page, windowWidth);
    const windowWidthInCss = windowWidth / PAGE_SCALE;
    const expectedContentWidthInCss = Math.min(CONTENT_MAX_WIDTH_IN_CSS, windowWidthInCss - 2 * SIDE_MARGIN_IN_CSS);
    const contentBox = await measureElementBoxOnScreen(page.getByRole('banner'));
    expect(contentBox.width / PAGE_SCALE).toBeCloseTo(expectedContentWidthInCss, 0);
    // Top bar and cards take the same band: the grid can be neither wider nor narrower than the top bar.
    const gridBox = await measureElementBoxOnScreen(page.getByRole('main'));
    expect(gridBox.left).toBeCloseTo(contentBox.left, 1);
    expect(gridBox.width).toBeCloseTo(contentBox.width, 1);
    expect(contentBox.left / PAGE_SCALE).toBeGreaterThanOrEqual(SIDE_MARGIN_IN_CSS - 0.5);
    // Centered: the empty band on the left equals the one on the right.
    expect(contentBox.left).toBeCloseTo(windowWidth - contentBox.right, 0);
    if (windowWidth === 1440) expect(contentBox.left / PAGE_SCALE).toBeCloseTo(SIDE_MARGIN_IN_CSS, 0);
    // At 1920 the empty band on each side used to be 328 px on screen (1360 px max width, 100%); now it is 168 px.
    if (windowWidth === 1920) expect(contentBox.left).toBeCloseTo((1920 - CONTENT_MAX_WIDTH_IN_CSS * PAGE_SCALE) / 2, 0);
    await page.screenshot({ path: test.info().outputPath(`ca-3-width-${windowWidth}.png`) });
  });
}

test('CA-4: the background has a green glow at the top and a thin grid that fades out from top to bottom', async ({ page }) => {
  await openOrderTicketAtWidth(page, 1440);
  const backgroundDrawing = await page.evaluate(() => {
    const backgroundStyle = getComputedStyle(document.body, '::before');
    return {
      content: backgroundStyle.content,
      height: backgroundStyle.height,
      images: backgroundStyle.backgroundImage,
      mask: backgroundStyle.maskImage,
      position: backgroundStyle.position,
      layer: backgroundStyle.zIndex,
      pointerEvents: backgroundStyle.pointerEvents,
    };
  });
  expect(backgroundDrawing).toMatchObject({ content: '""', height: '900px' });
  expect(backgroundDrawing.images).toContain('radial-gradient(60% 520px at 50% -140px, rgba(79, 227, 176, 0.11), rgba(0, 0, 0, 0) 70%)');
  expect(backgroundDrawing.images).toContain('linear-gradient(to right, rgba(232, 239, 238, 0.035) 1px, rgba(0, 0, 0, 0) 1px)');
  expect(backgroundDrawing.images).toContain('linear-gradient(rgba(232, 239, 238, 0.035) 1px, rgba(0, 0, 0, 0) 1px)');
  expect(backgroundDrawing.mask).toBe('linear-gradient(rgb(0, 0, 0) 0px, rgba(0, 0, 0, 0) 100%)');
  expect(backgroundDrawing).toMatchObject({ position: 'absolute', layer: '-1', pointerEvents: 'none' });

  // Proof on screen: at the top, on an empty spot, the pixel is greener than the plain background; at the bottom,
  // after the grid fades out (900 CSS px = 810 px on screen), the pixel is back to the plain background #081113.
  const screenScreenshot = await page.screenshot();
  const [topPixel, bottomPixel] = await page.evaluate(async (screenshotInBase64) => {
    const screenshotImage = new Image();
    screenshotImage.src = 'data:image/png;base64,' + screenshotInBase64;
    await screenshotImage.decode();
    const readingCanvas = document.createElement('canvas');
    readingCanvas.width = screenshotImage.width;
    readingCanvas.height = screenshotImage.height;
    const readingContext = readingCanvas.getContext('2d')!;
    readingContext.drawImage(screenshotImage, 0, 0);
    const readPixelColor = (positionX: number, positionY: number) => [...readingContext.getImageData(positionX, positionY, 1, 1).data.slice(0, 3)];
    return [readPixelColor(720, 4), readPixelColor(8, 990)];
  }, screenScreenshot.toString('base64'));
  // The canvas may shift 1 point per channel when converting the screenshot color.
  [8, 17, 19].forEach((plainBackgroundChannel, channelIndex) => expect(Math.abs(bottomPixel[channelIndex] - plainBackgroundChannel)).toBeLessThanOrEqual(1));
  expect(topPixel[1] - bottomPixel[1], `green at the top ${topPixel} vs at the bottom ${bottomPixel}`).toBeGreaterThanOrEqual(8);
});

for (const windowWidth of [861, 1280, 1440, 1920]) {
  test(`CA-7: at ${windowWidth} px, below the assets, the response card (wider) and the new order card (400 px) start and end at the same height`, async ({ page }) => {
    await openOrderTicketAtWidth(page, windowWidth);
    const leftCard = page.locator('section.response');
    const assetsBox = await measureElementBoxOnScreen(page.locator('section.exposure'));
    const leftCardBox = await measureElementBoxOnScreen(leftCard);
    const newOrderBox = await measureElementBoxOnScreen(page.getByRole('form', { name: 'Boleta de ordem' }));

    expect(leftCardBox.top).toBeGreaterThan(assetsBox.bottom);
    expect(leftCardBox.right).toBeLessThan(newOrderBox.left);
    expect(leftCardBox.width).toBeGreaterThan(newOrderBox.width);
    expect(newOrderBox.width / PAGE_SCALE).toBeCloseTo(NEW_ORDER_WIDTH_IN_CSS, 1);
    expect(leftCardBox.top - newOrderBox.top).toBe(0);
    expect(leftCardBox.bottom - newOrderBox.bottom).toBe(0);
    await expect(leftCard).toHaveCSS('border-radius', '22px');

    // "Exposição por ativo" takes the full width; the two bottom columns touch the content edges.
    const contentBox = await measureElementBoxOnScreen(page.getByRole('main'));
    expect(assetsBox.left).toBeCloseTo(contentBox.left, 1);
    expect(assetsBox.width).toBeCloseTo(contentBox.width, 1);
    expect(leftCardBox.left).toBeCloseTo(contentBox.left, 1);
    expect(newOrderBox.right).toBeCloseTo(contentBox.right, 1);
  });
}

const ASSET_SYMBOLS = ['PETR4', 'VALE3', 'VIIA4'];
// Each asset is a card 14 CSS px away from its neighbor (mockup 01), 12.6 px on screen.
const SPACE_BETWEEN_ASSETS_IN_CSS = 14;

for (const { windowWidth, isSideBySide } of [
  { windowWidth: 375, isSideBySide: false },
  { windowWidth: 860, isSideBySide: false },
  { windowWidth: 861, isSideBySide: true },
  { windowWidth: 1440, isSideBySide: true },
  { windowWidth: 1920, isSideBySide: true },
]) {
  test(`ASSUMI-09: at ${windowWidth} px the three assets sit ${isSideBySide ? 'side by side, with the same size' : 'one below the other, at full width'}`, async ({ page }) => {
    await openOrderTicketAtWidth(page, windowWidth);
    const assetListBox = await measureElementBoxOnScreen(page.locator('.exposure-list'));
    const assetBoxes = [];
    for (const assetSymbol of ASSET_SYMBOLS) assetBoxes.push(await measureElementBoxOnScreen(page.getByTestId(`exposicao-${assetSymbol}`)));
    const [firstAssetBox, secondAssetBox, thirdAssetBox] = assetBoxes;
    const spaceBetweenAssetsOnScreen = SPACE_BETWEEN_ASSETS_IN_CSS * PAGE_SCALE;
    if (isSideBySide) {
      expect(secondAssetBox.top).toBeCloseTo(firstAssetBox.top, 1);
      expect(thirdAssetBox.top).toBeCloseTo(firstAssetBox.top, 1);
      expect(firstAssetBox.left).toBeCloseTo(assetListBox.left, 1);
      expect(secondAssetBox.left).toBeCloseTo(firstAssetBox.right + spaceBetweenAssetsOnScreen, 1);
      expect(thirdAssetBox.left).toBeCloseTo(secondAssetBox.right + spaceBetweenAssetsOnScreen, 1);
      expect(thirdAssetBox.right).toBeCloseTo(assetListBox.right, 1);
      for (const assetBox of assetBoxes) expect(assetBox.width).toBeCloseTo((assetListBox.width - 2 * spaceBetweenAssetsOnScreen) / 3, 0);
    } else {
      expect(secondAssetBox.top).toBeCloseTo(firstAssetBox.bottom + spaceBetweenAssetsOnScreen, 1);
      expect(thirdAssetBox.top).toBeCloseTo(secondAssetBox.bottom + spaceBetweenAssetsOnScreen, 1);
      for (const assetBox of assetBoxes) {
        expect(assetBox.left).toBeCloseTo(assetListBox.left, 1);
        expect(assetBox.width).toBeCloseTo(assetListBox.width, 1);
      }
    }
  });
}

test('CA-40: opening the screen sends no request to another domain and downloads no image', async ({ page }) => {
  const requestedUrls: string[] = [];
  const requestedImages: string[] = [];
  page.on('request', (pageRequest) => {
    requestedUrls.push(pageRequest.url());
    if (pageRequest.resourceType() === 'image') requestedImages.push(pageRequest.url());
  });
  await openOrderTicketAtWidth(page, 1440);
  await page.waitForLoadState('networkidle');
  const screenOrigin = new URL(page.url()).origin;
  expect(requestedUrls.length).toBeGreaterThan(0);
  expect(requestedUrls.filter((requestedUrl) => new URL(requestedUrl).origin !== screenOrigin)).toEqual([]);
  expect(requestedImages).toEqual([]);
});
