import { expect, test, type Locator, type Page } from '@playwright/test';
import { CREATE_ORDER_ROUTE } from '../src/services/ordersService';

// BRIEF colors (CA-23, CA-24) as the browser returns them in the computed style.
const TRACK_COLOR = 'rgb(11, 23, 25)';
const TRACK_BORDER_COLOR = 'rgb(30, 50, 54)';
const SELECTED_SYMBOL_COLOR = 'rgb(232, 239, 238)';
const SELECTED_SYMBOL_TEXT_COLOR = 'rgb(8, 17, 19)';
const ACCENT_COLOR = 'rgb(79, 227, 176)';
const STEP_HIGHLIGHT_COLOR = 'rgb(18, 36, 39)';
const BUY_OPTION_GLOW = 'rgba(79, 227, 176, 0.35) 0px 0px 18px 0px';
const SELL_OPTION_GLOW = 'rgba(255, 164, 151, 0.32) 0px 0px 18px 0px';
const BUY_SUBMIT_GLOW = 'rgba(79, 227, 176, 0.45) 0px 10px 28px -6px';
const SELL_SUBMIT_GLOW = 'rgba(255, 164, 151, 0.42) 0px 10px 28px -6px';
const GREEN_FOCUS_RING = 'rgba(79, 227, 176, 0.22) 0px 0px 0px 3px';
// Path of ShieldIcon (Icons.tsx): ties the disclaimer to the shield and not to any icon.
const SHIELD_PATH = 'M12 3l7 3v5c0 4.5-3 8.3-7 10-4-1.7-7-5.5-7-10V6l7-3z';
const SYMBOLS_IN_ORDER = ['PETR4', 'VALE3', 'VIIA4'];

function locateOrderTicketForm(orderTicketPage: Page) {
  return orderTicketPage.getByRole('form', { name: 'Boleta de ordem' });
}

function locateSymbolTrack(orderTicketPage: Page) {
  return locateOrderTicketForm(orderTicketPage).getByRole('group', { name: 'Símbolo' });
}

function locateSymbolButton(orderTicketPage: Page, orderTicketSymbol: string) {
  return locateSymbolTrack(orderTicketPage).getByRole('button', { name: orderTicketSymbol, exact: true });
}

async function readComputedStyle(pageTarget: Locator, styleProperties: string[]) {
  return pageTarget.evaluate((elementOnPage, propertyNames) => {
    const elementStyle = getComputedStyle(elementOnPage);
    return Object.fromEntries(propertyNames.map((propertyName) => [propertyName, elementStyle.getPropertyValue(propertyName)]));
  }, styleProperties);
}

// With the page at 90% the browser rounds thin borders to the screen pixel: 1px of CSS comes back as 1.11px.
function readPixelMeasureNumber(valueInPixels: string) {
  return parseFloat(valueInPixels);
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

async function expectSelectedSymbolButton(orderTicketPage: Page, selectedSymbol: string) {
  for (const orderTicketSymbol of SYMBOLS_IN_ORDER) {
    await expect(locateSymbolButton(orderTicketPage, orderTicketSymbol), orderTicketSymbol).toHaveAttribute('aria-pressed', String(orderTicketSymbol === selectedSymbol));
  }
  await expect(locateOrderTicketForm(orderTicketPage).getByLabel(/^Quantidade de/)).toHaveAccessibleName(`Quantidade de ${selectedSymbol}`);
}

test.beforeEach(async ({ page }) => {
  await page.goto('/');
});

test('CA-23: the symbol is 3 side-by-side buttons, PETR4, VALE3 and VIIA4, with no dropdown list', async ({ page }) => {
  await expect(locateOrderTicketForm(page).locator('select')).toHaveCount(0);
  await expect(locateOrderTicketForm(page).getByRole('combobox')).toHaveCount(0);
  await expect(locateSymbolTrack(page).getByRole('button')).toHaveText(SYMBOLS_IN_ORDER);
  const topOfEachButton = [];
  for (const orderTicketSymbol of SYMBOLS_IN_ORDER) {
    await expect(locateSymbolButton(page, orderTicketSymbol)).toBeVisible();
    topOfEachButton.push((await locateSymbolButton(page, orderTicketSymbol).boundingBox())!.y);
  }
  expect(new Set(topOfEachButton).size).toBe(1);
  await expectSelectedSymbolButton(page, 'PETR4');
});

test('CA-23: the track is dark and the selected symbol turns light, with the BRIEF values', async ({ page }) => {
  const trackStyle = await readComputedStyle(locateSymbolTrack(page), ['background-color', 'border-top-color', 'border-top-width', 'border-top-left-radius', 'padding-top', 'padding-left']);
  expect(trackStyle).toMatchObject({
    'background-color': TRACK_COLOR,
    'border-top-color': TRACK_BORDER_COLOR,
    'border-top-left-radius': '16px',
    'padding-top': '6px',
    'padding-left': '6px',
  });
  expect(readPixelMeasureNumber(trackStyle['border-top-width'])).toBeCloseTo(1, 0);
  await locateSymbolButton(page, 'VIIA4').click();
  await expectSelectedSymbolButton(page, 'VIIA4');
  await expect.poll(() => readComputedStyle(locateSymbolButton(page, 'VIIA4'), ['background-color', 'color', 'border-top-left-radius'])).toEqual({
    'background-color': SELECTED_SYMBOL_COLOR,
    color: SELECTED_SYMBOL_TEXT_COLOR,
    'border-top-left-radius': '11px',
  });
  await expect.poll(() => readComputedStyle(locateSymbolButton(page, 'PETR4'), ['background-color'])).toEqual({ 'background-color': 'rgba(0, 0, 0, 0)' });
});

test('CA-23: clicking VIIA4 changes the label and the order goes out with VIIA4', async ({ page }) => {
  await locateSymbolButton(page, 'VIIA4').click();
  await expectSelectedSymbolButton(page, 'VIIA4');
  await locateOrderTicketForm(page).getByLabel(/^Quantidade de/).fill('1');
  await locateOrderTicketForm(page).getByLabel('Preço por ação (R$)').fill('1,00');
  const createOrderRequest = page.waitForRequest((httpRequest) => httpRequest.method() === 'POST' && new URL(httpRequest.url()).pathname === CREATE_ORDER_ROUTE);
  const createOrderResponse = page.waitForResponse((httpResponse) => httpResponse.request().method() === 'POST' && new URL(httpResponse.url()).pathname === CREATE_ORDER_ROUTE);
  await locateOrderTicketForm(page).getByRole('button', { name: 'Enviar ordem de compra' }).click();
  expect((await createOrderRequest).postDataJSON()).toEqual({ symbol: 'VIIA4', side: 'buy', quantity: 1, price: 1 });
  const serverResponse = await createOrderResponse;
  expect(serverResponse.status()).toBe(200);
  expect((await serverResponse.json()).data.symbol).toBe('VIIA4');
});

test('CA-23/CA-27: with the keyboard, Tab reaches the symbols, Enter and Space select, and the focus shows', async ({ page }) => {
  await locateOrderTicketForm(page).getByRole('button', { name: 'Venda', exact: true }).focus();
  for (const orderTicketSymbol of SYMBOLS_IN_ORDER) {
    await page.keyboard.press('Tab');
    await expect(locateSymbolButton(page, orderTicketSymbol)).toBeFocused();
    const focusOutline = await readComputedStyle(locateSymbolButton(page, orderTicketSymbol), ['outline-style', 'outline-color', 'outline-width']);
    expect(focusOutline, orderTicketSymbol).toMatchObject({ 'outline-style': 'solid', 'outline-color': ACCENT_COLOR });
    expect(readPixelMeasureNumber(focusOutline['outline-width']), orderTicketSymbol).toBeGreaterThanOrEqual(1);
  }
  await page.keyboard.press('Enter');
  await expectSelectedSymbolButton(page, 'VIIA4');
  await page.keyboard.press('Shift+Tab');
  await expect(locateSymbolButton(page, 'VALE3')).toBeFocused();
  await page.keyboard.press(' ');
  await expectSelectedSymbolButton(page, 'VALE3');
});

test('CA-27 (RNF-08a): with the keyboard, the focus of − and + shows inside the button, with a highlight background', async ({ page }) => {
  const quantitySteps = [
    locateOrderTicketForm(page).getByRole('button', { name: 'Diminuir quantidade' }),
    locateOrderTicketForm(page).getByRole('button', { name: 'Aumentar quantidade' }),
  ];
  await page.mouse.move(0, 0);
  await locateSymbolButton(page, 'VIIA4').focus();
  await page.keyboard.press('Tab');
  await expect(quantitySteps[0]).toBeFocused();
  for (const [stepIndex, focusedStep] of quantitySteps.entries()) {
    if (stepIndex === 1) {
      await page.keyboard.press('Tab');
      await expect(locateOrderTicketForm(page).getByLabel(/^Quantidade de/)).toBeFocused();
      await page.keyboard.press('Tab');
      await expect(focusedStep).toBeFocused();
    }
    const unfocusedStep = quantitySteps[1 - stepIndex];
    await expect.poll(() => readComputedStyle(focusedStep, ['background-color', 'outline-style', 'outline-color'])).toEqual({
      'background-color': STEP_HIGHLIGHT_COLOR,
      'outline-style': 'solid',
      'outline-color': ACCENT_COLOR,
    });
    const stepOutline = await readComputedStyle(focusedStep, ['outline-offset', 'outline-width']);
    // Inward offset larger than the thickness: the whole outline fits in the button and the box does not clip it.
    expect(readPixelMeasureNumber(stepOutline['outline-offset'])).toBeLessThanOrEqual(-readPixelMeasureNumber(stepOutline['outline-width']));
    await expect.poll(() => readComputedStyle(unfocusedStep, ['background-color'])).toEqual({ 'background-color': TRACK_COLOR });
  }
});

test('CA-27: the text of the symbol buttons has a contrast of at least 4.5:1, selected or not', async ({ page }) => {
  const trackColor = (await readComputedStyle(locateSymbolTrack(page), ['background-color']))['background-color'];
  for (const orderTicketSymbol of SYMBOLS_IN_ORDER) {
    await locateSymbolButton(page, orderTicketSymbol).click();
    await expectSelectedSymbolButton(page, orderTicketSymbol);
    await page.mouse.move(0, 0);
    await expect.poll(() => readComputedStyle(locateSymbolButton(page, orderTicketSymbol), ['background-color'])).toEqual({ 'background-color': SELECTED_SYMBOL_COLOR });
    for (const unselectedSymbol of SYMBOLS_IN_ORDER.filter((listedSymbol) => listedSymbol !== orderTicketSymbol)) {
      await expect.poll(() => readComputedStyle(locateSymbolButton(page, unselectedSymbol), ['background-color'])).toEqual({ 'background-color': 'rgba(0, 0, 0, 0)' });
    }
    for (const comparedSymbol of SYMBOLS_IN_ORDER) {
      const buttonStyle = await readComputedStyle(locateSymbolButton(page, comparedSymbol), ['color', 'background-color']);
      const buttonBackgroundColor = buttonStyle['background-color'] === 'rgba(0, 0, 0, 0)' ? trackColor : buttonStyle['background-color'];
      expect(calculateWcagContrast(buttonStyle.color, buttonBackgroundColor), `${comparedSymbol} with ${orderTicketSymbol} selected`).toBeGreaterThanOrEqual(4.5);
    }
  }
});

test('CA-24: Compra/Venda toggle on the dark track with 16px corners, with a green glow on buy and coral on sell', async ({ page }) => {
  const sideToggle = locateOrderTicketForm(page).getByRole('group', { name: 'Lado da ordem' });
  expect(await readComputedStyle(sideToggle, ['background-color', 'border-top-left-radius'])).toEqual({ 'background-color': TRACK_COLOR, 'border-top-left-radius': '16px' });
  await expect.poll(async () => (await readComputedStyle(sideToggle.getByRole('button', { name: 'Compra' }), ['box-shadow']))['box-shadow']).toBe(BUY_OPTION_GLOW);
  await sideToggle.getByRole('button', { name: 'Venda' }).click();
  await expect.poll(async () => (await readComputedStyle(sideToggle.getByRole('button', { name: 'Venda' }), ['box-shadow']))['box-shadow']).toBe(SELL_OPTION_GLOW);
  await expect.poll(async () => (await readComputedStyle(sideToggle.getByRole('button', { name: 'Compra' }), ['box-shadow']))['box-shadow']).toBe('none');
});

test('CA-24: labels in small caps, fields with 14px corners and a green ring on focus', async ({ page }) => {
  for (const fieldLabel of [locateOrderTicketForm(page).getByText('Símbolo', { exact: true }), locateOrderTicketForm(page).getByText('Quantidade de PETR4', { exact: true }), locateOrderTicketForm(page).getByText('Preço por ação (R$)', { exact: true })]) {
    expect(await readComputedStyle(fieldLabel, ['text-transform', 'font-size'])).toEqual({ 'text-transform': 'uppercase', 'font-size': '12px' });
  }
  const priceField = locateOrderTicketForm(page).getByLabel('Preço por ação (R$)');
  const quantityFrame = locateOrderTicketForm(page).locator('.quantity-stepper');
  expect((await readComputedStyle(priceField, ['border-top-left-radius']))['border-top-left-radius']).toBe('14px');
  expect((await readComputedStyle(quantityFrame, ['border-top-left-radius']))['border-top-left-radius']).toBe('14px');
  await priceField.focus();
  await expect.poll(async () => readComputedStyle(priceField, ['border-top-color', 'box-shadow'])).toMatchObject({ 'border-top-color': ACCENT_COLOR });
  await expect.poll(async () => (await readComputedStyle(priceField, ['box-shadow']))['box-shadow']).toBe(GREEN_FOCUS_RING);
  await locateOrderTicketForm(page).getByLabel(/^Quantidade de/).focus();
  await expect.poll(async () => (await readComputedStyle(quantityFrame, ['box-shadow']))['box-shadow']).toBe(GREEN_FOCUS_RING);
});

test('CA-24: quantity as a large Sora 22px number and summary in a box with the total as a large number', async ({ page }) => {
  const quantityStyle = await readComputedStyle(locateOrderTicketForm(page).getByLabel(/^Quantidade de/), ['font-family', 'font-size']);
  expect(quantityStyle['font-family']).toMatch(/^"?Sora"?,/);
  expect(quantityStyle['font-size']).toBe('22px');
  await locateOrderTicketForm(page).getByLabel('Preço por ação (R$)').fill('100');
  const estimatedTotal = page.getByTestId('total-estimado');
  await expect(estimatedTotal).toHaveText('R$ 10.000,00');
  const totalStyle = await readComputedStyle(estimatedTotal, ['font-family', 'font-size']);
  expect(totalStyle['font-family']).toMatch(/^"?Sora"?,/);
  expect(totalStyle['font-size']).toBe('22px');
  const summaryBox = await estimatedTotal.evaluate((totalOnPage) => {
    const summaryStyle = getComputedStyle(totalOnPage.closest('dl')!);
    return {
      background: summaryStyle.backgroundColor,
      borderColor: summaryStyle.borderTopColor,
      borderWidth: summaryStyle.borderTopWidth,
      corner: summaryStyle.borderTopLeftRadius,
      summaryLabels: Array.from(totalOnPage.closest('dl')!.querySelectorAll('dt')).map((summaryLabel) => summaryLabel.textContent),
    };
  });
  expect(summaryBox).toMatchObject({ background: TRACK_COLOR, borderColor: TRACK_BORDER_COLOR, corner: '16px', summaryLabels: ['Preço por ação', 'Valor total estimado'] });
  expect(readPixelMeasureNumber(summaryBox.borderWidth)).toBeCloseTo(1, 0);
});

test('CA-24: submit button 52px tall, 14px corners and a glow in the side color', async ({ page }) => {
  const submitButton = locateOrderTicketForm(page).getByRole('button', { name: 'Enviar ordem de compra' });
  const buySubmitButtonStyle = await readComputedStyle(submitButton, ['height', 'border-top-left-radius', 'box-shadow']);
  expect(readPixelMeasureNumber(buySubmitButtonStyle.height)).toBeCloseTo(52, 1);
  expect(buySubmitButtonStyle['border-top-left-radius']).toBe('14px');
  expect(buySubmitButtonStyle['box-shadow']).toBe(BUY_SUBMIT_GLOW);
  await locateOrderTicketForm(page).getByRole('group', { name: 'Lado da ordem' }).getByRole('button', { name: 'Venda' }).click();
  await expect.poll(async () => (await readComputedStyle(locateOrderTicketForm(page).getByRole('button', { name: 'Enviar ordem de venda' }), ['box-shadow']))['box-shadow']).toBe(SELL_SUBMIT_GLOW);
});

test('CA-24/CA-25: the demo disclaimer sits in a box with the shield and the usual text', async ({ page }) => {
  const demoDisclaimer = locateOrderTicketForm(page).getByText('Ordem de demonstração: nenhuma operação real é feita.', { exact: true });
  await expect(demoDisclaimer).toHaveCount(1);
  await expect(demoDisclaimer.locator('svg[aria-hidden="true"]')).toHaveCount(1);
  await expect(demoDisclaimer.locator('svg')).toBeVisible();
  await expect(demoDisclaimer.locator('svg path')).toHaveAttribute('d', SHIELD_PATH);
  const disclaimerStyle = await readComputedStyle(demoDisclaimer, ['background-color', 'border-top-color', 'border-top-width', 'border-top-left-radius']);
  expect(disclaimerStyle).toMatchObject({ 'background-color': TRACK_COLOR, 'border-top-color': TRACK_BORDER_COLOR, 'border-top-left-radius': '14px' });
  expect(readPixelMeasureNumber(disclaimerStyle['border-top-width'])).toBeCloseTo(1, 0);
});

for (const viewportWidth of [375, 860]) {
  test(`CA-26: at ${viewportWidth} px the 3 symbols and the order ticket fit without clipping and without horizontal scrolling`, async ({ page }) => {
    await page.setViewportSize({ width: viewportWidth, height: 900 });
    await page.goto('/');
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(viewportWidth);
    const orderTicketBox = (await locateOrderTicketForm(page).boundingBox())!;
    expect(orderTicketBox.x).toBeGreaterThanOrEqual(0);
    expect(orderTicketBox.x + orderTicketBox.width).toBeLessThanOrEqual(viewportWidth);
    const topOfEachButton = [];
    for (const orderTicketSymbol of SYMBOLS_IN_ORDER) {
      const assetButton = locateSymbolButton(page, orderTicketSymbol);
      await expect(assetButton).toBeVisible();
      const buttonBox = (await assetButton.boundingBox())!;
      topOfEachButton.push(buttonBox.y);
      expect(buttonBox.x, `${orderTicketSymbol}: left`).toBeGreaterThanOrEqual(orderTicketBox.x);
      expect(buttonBox.x + buttonBox.width, `${orderTicketSymbol}: right`).toBeLessThanOrEqual(orderTicketBox.x + orderTicketBox.width);
      const textFitsInButton = await assetButton.evaluate((buttonOnPage) => buttonOnPage.scrollWidth <= buttonOnPage.clientWidth);
      expect(textFitsInButton, `${orderTicketSymbol}: whole text`).toBe(true);
    }
    expect(new Set(topOfEachButton).size).toBe(1);
  });
}
