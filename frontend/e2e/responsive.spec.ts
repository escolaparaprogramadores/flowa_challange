import { expect, test, type Locator, type Page } from '@playwright/test';
import { CREATE_ORDER_ROUTE } from '../src/services/ordersService';

// Page shell breakpoints (CA-7 and CA-26): one column up to 860 px and two from 861 px.
// The boundary is measured on both sides; 375, 1440 and 1920 are the round's proof widths.
const THEME_WIDTHS = [
  { width: 375, expectedColumns: 1 },
  { width: 390, expectedColumns: 1 },
  { width: 860, expectedColumns: 1 },
  { width: 861, expectedColumns: 2 },
  { width: 1280, expectedColumns: 2 },
  { width: 1440, expectedColumns: 2 },
  { width: 1920, expectedColumns: 2 },
];
const CA_23_WIDTHS = [390, 860, 1280];
const ACCENT_COLOR = 'rgb(79, 227, 176)';
const MUTED_TEXT_COLOR = 'rgb(157, 176, 174)';

// WCAG 2 contrast ratio between two "rgb(r, g, b)" colors (relative luminance).
function calculateWcagContrast(textColor: string, backgroundColor: string) {
  const colorLuminance = (rgbColor: string) => {
    const [redChannel, greenChannel, blueChannel] = (rgbColor.match(/\d+(\.\d+)?/g) ?? []).slice(0, 3).map(Number).map((channelFrom0To255) => {
      const channelFrom0To1 = channelFrom0To255 / 255;
      return channelFrom0To1 <= 0.03928 ? channelFrom0To1 / 12.92 : ((channelFrom0To1 + 0.055) / 1.055) ** 2.4;
    });
    return 0.2126 * redChannel + 0.7152 * greenChannel + 0.0722 * blueChannel;
  };
  const [higherLuminance, lowerLuminance] = [colorLuminance(textColor), colorLuminance(backgroundColor)].sort((firstLuminance, secondLuminance) => secondLuminance - firstLuminance);
  return (higherLuminance + 0.05) / (lowerLuminance + 0.05);
}

async function countLayoutGridColumns(orderTicketPage: Page) {
  return orderTicketPage.locator('.layout-grid').evaluate((pageLayoutGrid) => getComputedStyle(pageLayoutGrid).gridTemplateColumns.split(' ').length);
}

function orderTicketControls(orderTicketPage: Page): Array<[string, Locator]> {
  return [
    ['Compra', orderTicketPage.getByRole('button', { name: 'Compra', exact: true })],
    ['Venda', orderTicketPage.getByRole('button', { name: 'Venda', exact: true })],
    ['Símbolo PETR4', orderTicketPage.getByRole('group', { name: 'Símbolo' }).getByRole('button', { name: 'PETR4', exact: true })],
    ['Símbolo VALE3', orderTicketPage.getByRole('group', { name: 'Símbolo' }).getByRole('button', { name: 'VALE3', exact: true })],
    ['Símbolo VIIA4', orderTicketPage.getByRole('group', { name: 'Símbolo' }).getByRole('button', { name: 'VIIA4', exact: true })],
    ['Diminuir quantidade', orderTicketPage.getByRole('button', { name: 'Diminuir quantidade' })],
    ['Quantidade', orderTicketPage.getByLabel(/^Quantidade de/)],
    ['Aumentar quantidade', orderTicketPage.getByRole('button', { name: 'Aumentar quantidade' })],
    ['Preço', orderTicketPage.getByLabel('Preço por ação (R$)')],
    ['Enviar', orderTicketPage.getByRole('button', { name: /^Enviar ordem/ })],
  ];
}

for (const { width: viewportWidth, expectedColumns } of THEME_WIDTHS) {
  test(`RNF-03: at ${viewportWidth} px the page does not scroll sideways and the grid has ${expectedColumns} column(s)`, async ({ page }) => {
    await page.setViewportSize({ width: viewportWidth, height: 900 });
    await page.goto('/');
    await expect(page.getByRole('heading', { name: 'Boleta de ordens' })).toBeVisible();
    const contentWidth = await page.evaluate(() => document.documentElement.scrollWidth);
    expect(contentWidth).toBe(viewportWidth);
    expect(await countLayoutGridColumns(page)).toBe(expectedColumns);
  });
}

test('RF-01/RF-02 and CA-8: single page without login, with logo, exposure, Compra/Venda and order ticket, without menu', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByRole('img', { name: 'Base investimentos' })).toBeVisible();
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Boleta de ordens');
  await expect(page.getByRole('heading', { level: 2 })).toHaveText(['Exposição por ativo', 'Compra/Venda', 'Nova ordem']);
  await expect(page.getByText('Resposta da ordem')).toHaveCount(0);
  await expect(page.getByRole('form', { name: 'Boleta de ordem' })).toBeVisible();
  await expect(page.locator('input[type="password"]')).toHaveCount(0);
  await expect(page.getByRole('navigation')).toHaveCount(0);
  // The order list now exists (CA-11): it is the Compra/Venda table or, with an empty database, the empty notice.
  const orderListCard = page.getByRole('region', { name: 'Compra/Venda' });
  await expect(orderListCard.getByRole('table').or(orderListCard.getByTestId('lista-de-ordens-vazia'))).toHaveCount(1);
  await expect(page.getByRole('table')).toHaveCount(await orderListCard.getByRole('table').count());
  // The Datadog links live in the top bar (CA-9, F4); there is no link inside the content.
  await expect(page.getByRole('main').getByRole('link')).toHaveCount(0);
});

for (const viewportWidth of [375, 860]) {
  test(`G-1 and CA-26: at ${viewportWidth} px the stacked order is assets, Nova ordem and Compra/Venda, with nothing off screen`, async ({ page }) => {
    // Only with a filled list could a wide table overflow the card: makes sure at least one order is stored.
    const storedOrderResponse = await page.request.post('/api/orders', { data: { symbol: 'PETR4', side: 'buy', quantity: 1, price: 10 } });
    expect(storedOrderResponse.status()).toBe(200);
    await page.setViewportSize({ width: viewportWidth, height: 900 });
    await page.goto('/');
    await expect(page.getByTestId('exposicao-PETR4')).toBeVisible();
    const exposureBox = (await page.locator('section.exposure').boundingBox())!;
    const newOrderBox = (await page.getByRole('form', { name: 'Boleta de ordem' }).boundingBox())!;
    const responseBox = (await page.locator('section.response').boundingBox())!;
    expect(exposureBox.y + exposureBox.height).toBeLessThanOrEqual(newOrderBox.y);
    expect(newOrderBox.y + newOrderBox.height).toBeLessThanOrEqual(responseBox.y);
    const cardBoxes = [
      ['Exposure', exposureBox],
      ['Nova ordem', newOrderBox],
      ['Response', responseBox],
    ] as const;
    for (const [cardName, cardBox] of cardBoxes) {
      expect(cardBox.x, `${cardName}: starts inside the screen`).toBeGreaterThanOrEqual(0);
      expect(cardBox.x + cardBox.width, `${cardName}: ends inside the screen`).toBeLessThanOrEqual(viewportWidth);
    }
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(viewportWidth);

    // Nothing clipped inside: the cards hide what overflows (overflow: hidden), so no card may have
    // content wider or taller than itself, and every visible piece inside it must fit in the card and in the viewport.
    const cardsCheck = await page.getByRole('main').evaluate((pageContent, screenWidth) => {
      const problemsFound: string[] = [];
      const checkedPiecesPerCard: Record<string, number> = {};
      for (const pageCard of pageContent.querySelectorAll<HTMLElement>(':scope > .panel, :scope > .card')) {
        const cardName = pageCard.className;
        if (pageCard.scrollWidth > pageCard.clientWidth) problemsFound.push(`${cardName}: content wider than the card`);
        if (pageCard.scrollHeight > pageCard.clientHeight) problemsFound.push(`${cardName}: content taller than the card`);
        const cardBox = pageCard.getBoundingClientRect();
        checkedPiecesPerCard[cardName] = 0;
        for (const cardPiece of pageCard.querySelectorAll<HTMLElement>('*')) {
          const pieceBox = cardPiece.getBoundingClientRect();
          if (pieceBox.width === 0 || pieceBox.height === 0 || cardPiece.closest('.screen-reader-only')) continue;
          checkedPiecesPerCard[cardName] += 1;
          const fitsInCard = pieceBox.left >= cardBox.left - 0.5 && pieceBox.right <= cardBox.right + 0.5 && pieceBox.top >= cardBox.top - 0.5 && pieceBox.bottom <= cardBox.bottom + 0.5;
          const fitsOnScreen = pieceBox.left >= -0.5 && pieceBox.right <= screenWidth + 0.5;
          if (!fitsInCard || !fitsOnScreen) problemsFound.push(`${cardName} > ${cardPiece.tagName}.${cardPiece.className}: outside the card or the screen`);
        }
      }
      return { problemsFound, checkedPiecesPerCard };
    }, viewportWidth);
    expect(cardsCheck.problemsFound).toEqual([]);
    expect(Object.keys(cardsCheck.checkedPiecesPerCard)).toEqual(['panel exposure', 'card response order-list', 'card order-ticket']);

    // Each required piece, by name: visible, with size and fully inside its card and the viewport.
    // A piece that vanished (width 0) must not pass just because the other pieces of the card are still there.
    const exposureCard = page.locator('section.exposure');
    const newOrderCard = page.getByRole('form', { name: 'Boleta de ordem' });
    const responseCard = page.locator('section.response');
    const requiredPieces: Array<[string, Locator, Locator]> = [
      ['Exposure title', exposureCard.getByRole('heading', { name: 'Exposição por ativo' }), exposureCard],
      ...['PETR4', 'VALE3', 'VIIA4'].flatMap((assetSymbol): Array<[string, Locator, Locator]> => [
        [`${assetSymbol}: current exposure`, page.getByTestId(`exposicao-${assetSymbol}`).getByTestId('exposicao-atual'), exposureCard],
        [`${assetSymbol}: remaining to limit`, page.getByTestId(`exposicao-${assetSymbol}`).getByTestId('exposicao-restante'), exposureCard],
      ]),
      ['Nova ordem title', newOrderCard.getByRole('heading', { name: 'Nova ordem' }), newOrderCard],
      ...orderTicketControls(page).map(([controlName, orderTicketControl]): [string, Locator, Locator] => [controlName, orderTicketControl, newOrderCard]),
      ['Compra/Venda title', responseCard.getByRole('heading', { name: 'Compra/Venda' }), responseCard],
      ...['date', 'status', 'asset', 'side', 'quantity', 'price', 'order-number', 'send-identifier'].map((orderListColumn): [string, Locator, Locator] => [
        `Compra/Venda: ${orderListColumn} of the top order`,
        responseCard.getByTestId('linha-da-ordem').first().locator(`td[data-column="${orderListColumn}"]`),
        responseCard,
      ]),
    ];
    for (const [pieceName, requiredPiece, pieceCard] of requiredPieces) {
      await expect(requiredPiece, pieceName).toHaveCount(1);
      await expect(requiredPiece, pieceName).toBeVisible();
      const pieceBox = (await requiredPiece.boundingBox())!;
      const pieceCardBox = (await pieceCard.boundingBox())!;
      expect(pieceBox.width, `${pieceName}: width`).toBeGreaterThan(0);
      expect(pieceBox.height, `${pieceName}: height`).toBeGreaterThan(0);
      expect(pieceBox.x, `${pieceName}: starts inside the card`).toBeGreaterThanOrEqual(pieceCardBox.x - 0.5);
      expect(pieceBox.x + pieceBox.width, `${pieceName}: ends inside the card`).toBeLessThanOrEqual(pieceCardBox.x + pieceCardBox.width + 0.5);
      expect(pieceBox.x + pieceBox.width, `${pieceName}: ends inside the screen`).toBeLessThanOrEqual(viewportWidth + 0.5);
    }
    // No fullPage: with the 90% zoom Playwright measures the whole page without the scale and the screenshot comes out wider.
    await page.screenshot({ path: test.info().outputPath(`ca-26-stacked-${viewportWidth}.png`) });
  });
}

// The page opens at 90% (CA-2, owner decision 7): the box on screen is 0.9 of the CSS size.
// The touch area is checked in CSS px, the size the code controls (F3 spec, ASSUMI-03).
for (const viewportWidth of CA_23_WIDTHS) {
  test(`RNF-05: at ${viewportWidth} px each order ticket control has a touch area of at least 44 CSS px`, async ({ page }) => {
    await page.setViewportSize({ width: viewportWidth, height: 900 });
    await page.goto('/');
    const pageScale = Number(await page.evaluate(() => getComputedStyle(document.documentElement).zoom));
    expect(pageScale).toBe(0.9);
    for (const [controlName, orderTicketControl] of orderTicketControls(page)) {
      await expect(orderTicketControl, controlName).toHaveCount(1);
      const controlBox = (await orderTicketControl.boundingBox())!;
      // The on-screen box is rounded to 1/64 px; the tolerance is that rounding, converted to CSS px.
      const roundingToleranceInCss = 1 / 64 / pageScale;
      expect(controlBox.height / pageScale + roundingToleranceInCss, `${controlName}: height`).toBeGreaterThanOrEqual(44);
      expect(controlBox.width / pageScale + roundingToleranceInCss, `${controlName}: width`).toBeGreaterThanOrEqual(44);
    }
  });
}

test('RNF-05: keyboard focus is visible on each order ticket control, in the accent color', async ({ page }) => {
  await page.goto('/');
  for (const [controlName, orderTicketControl] of orderTicketControls(page)) {
    await orderTicketControl.focus();
    // The border changes with a 180 ms transition; waits for the color to settle instead of reading it halfway.
    await expect
      .poll(
        () =>
          orderTicketControl.evaluate((controlOnPage) => {
            const controlStyle = getComputedStyle(controlOnPage);
            const quantityStepperFrame = controlOnPage.closest('.quantity-stepper');
            const focusOutlineColor = controlStyle.outlineStyle === 'solid' ? controlStyle.outlineColor : '';
            const focusBorderColor = (quantityStepperFrame ? getComputedStyle(quantityStepperFrame) : controlStyle).borderTopColor;
            return [focusOutlineColor, focusBorderColor];
          }),
        { message: controlName },
      )
      .toContain(ACCENT_COLOR);
  }
});

test('RNF-04: the price placeholder text uses the theme muted color, with contrast of at least 4.5:1', async ({ page }) => {
  await page.goto('/');
  const priceField = page.getByLabel('Preço por ação (R$)');
  const placeholderColors = await priceField.evaluate((fieldOnPage) => ({
    textColor: getComputedStyle(fieldOnPage, '::placeholder').color,
    backgroundColor: getComputedStyle(fieldOnPage).backgroundColor,
  }));
  expect(placeholderColors.textColor).toBe(MUTED_TEXT_COLOR);
  expect(calculateWcagContrast(placeholderColors.textColor, placeholderColors.backgroundColor)).toBeGreaterThanOrEqual(4.5);
});

test('RNF-01: the page declares the dark color scheme, so selection, scrolling and native controls follow the theme', async ({ page }) => {
  await page.goto('/');
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe('dark');
});

for (const viewportWidth of [390, 1024, 1179, 1280]) {
  test(`RNF-03: at ${viewportWidth} px the exposure panel labels fit on one line`, async ({ page }) => {
    await page.setViewportSize({ width: viewportWidth, height: 900 });
    await page.goto('/');
    for (const exposureSymbol of ['PETR4', 'VALE3', 'VIIA4']) {
      for (const exposureLabel of ['Exposição atual', 'Falta até o limite']) {
        const symbolLabel = page.getByTestId('exposicao-' + exposureSymbol).locator('dt', { hasText: exposureLabel });
        await expect(symbolLabel).toHaveCount(1);
        // One line is shorter than twice the font size; wrapped onto two lines, it goes beyond that.
        const fitsOnOneLine = await symbolLabel.evaluate(
          (labelOnPage) => labelOnPage.getBoundingClientRect().height < parseFloat(getComputedStyle(labelOnPage).fontSize) * 2,
        );
        expect(fitsOnOneLine, `${exposureSymbol} / ${exposureLabel}`).toBe(true);
      }
    }
  });
}

test('RNF-02: the theme fonts Sora and Manrope load and numbers use tabular figures', async ({ page }) => {
  await page.goto('/');
  const loadedFonts = await page.evaluate(async () => {
    await document.fonts.ready;
    return [...document.fonts]
      .filter((loadedFont) => loadedFont.status === 'loaded')
      .map((loadedFont) => `${loadedFont.family.replaceAll('"', '')} ${loadedFont.weight}`);
  });
  expect(loadedFonts).toContain('Sora 600');
  expect(loadedFonts).toContain('Manrope 400');
  await expect(page.getByRole('heading', { name: 'Boleta de ordens' })).toHaveCSS('font-family', /^Sora/);
  await expect(page.locator('body')).toHaveCSS('font-family', /^Manrope/);
  await expect(page.getByTestId('total-estimado')).toHaveCSS('font-variant-numeric', 'tabular-nums');
});

test('RF-08: at 375 px, while "Enviando…" shows, "Deletar tudo" stays inside the Compra/Venda card and the page does not scroll sideways', async ({ page }) => {
  await page.setViewportSize({ width: 375, height: 900 });
  await page.goto('/');
  let releaseHeldOrderCreation!: () => void;
  const heldOrderCreationReleased = new Promise<void>((releaseHeldRequest) => { releaseHeldOrderCreation = releaseHeldRequest; });
  await page.route('**' + CREATE_ORDER_ROUTE, async (heldOrderCreationRoute) => {
    await heldOrderCreationReleased;
    await heldOrderCreationRoute.continue();
  });
  await page.getByLabel(/^Quantidade de/).fill('1');
  await page.getByLabel('Preço por ação (R$)').fill('10,00');
  await page.getByRole('button', { name: 'Enviar ordem de compra' }).click();

  const orderListCard = page.locator('section.order-list');
  await expect(orderListCard.getByTestId('selo-enviando')).toBeVisible();
  const deleteAllButton = orderListCard.getByRole('button', { name: 'Deletar tudo' });
  await expect(deleteAllButton).toBeVisible();
  const pageWidths = await page.evaluate(() => ({ scrollWidth: document.documentElement.scrollWidth, clientWidth: document.documentElement.clientWidth }));
  expect(pageWidths.scrollWidth).toBe(pageWidths.clientWidth);
  const orderListCardBox = (await orderListCard.boundingBox())!;
  const deleteAllButtonBox = (await deleteAllButton.boundingBox())!;
  expect(deleteAllButtonBox.x).toBeGreaterThanOrEqual(orderListCardBox.x);
  expect(deleteAllButtonBox.x + deleteAllButtonBox.width).toBeLessThanOrEqual(orderListCardBox.x + orderListCardBox.width);

  releaseHeldOrderCreation();
  await expect(orderListCard.getByTestId('selo-enviando')).toHaveCount(0);
});
