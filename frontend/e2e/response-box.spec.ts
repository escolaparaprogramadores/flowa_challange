import { expect, test, type Page } from '@playwright/test';
import { CREATE_ORDER_ROUTE, ORDERS_ROUTE } from '../src/services/ordersService';

// CA-3 against the real OrderGenerator and OrderAccumulator: the box above the "Compra/Venda" table shows the answer of
// every send. The test starts from "Deletar tudo" so the PETR4 exposure is zero and the second big buy breaks the limit.

const ACCEPTED_BOX_COLORS = { border: 'rgb(111, 235, 192)', background: 'rgba(79, 227, 176, 0.13)' };
const REJECTED_BOX_COLORS = { border: 'rgb(255, 164, 151)', background: 'rgba(255, 138, 122, 0.12)' };
const SCREENSHOT_FOLDER = process.env.E2E_PROVAS_DIR;

function locateOrderListCard(orderTicketPage: Page) {
  return orderTicketPage.getByRole('region', { name: 'Compra/Venda' });
}

async function deleteAllOrdersOnServer(orderTicketPage: Page) {
  const deleteResponse = await orderTicketPage.request.delete(ORDERS_ROUTE);
  expect(deleteResponse.status()).toBe(204);
}

async function sendPetr4BigBuy(orderTicketPage: Page) {
  await orderTicketPage.getByRole('group', { name: 'Símbolo' }).getByRole('button', { name: 'PETR4', exact: true }).click();
  await orderTicketPage.getByRole('group', { name: 'Lado da ordem' }).getByRole('button', { name: 'Compra' }).click();
  await orderTicketPage.getByLabel(/^Quantidade de/).fill('99.999');
  await orderTicketPage.getByLabel('Preço por ação (R$)').fill('999,99');
  const createOrderResponse = orderTicketPage.waitForResponse(
    (httpResponse) => httpResponse.request().method() === 'POST' && new URL(httpResponse.url()).pathname === CREATE_ORDER_ROUTE,
  );
  await orderTicketPage.getByRole('button', { name: /^Enviar ordem/ }).click();
  const orderDataMessage = (await (await createOrderResponse).json()) as { message: string; data: { status: string } };
  await expect(orderTicketPage.getByRole('button', { name: /^Enviar ordem/ })).toBeEnabled();
  return orderDataMessage;
}

async function expectBoxAboveTable(orderTicketPage: Page, responseBoxTestId: string) {
  const responseBox = locateOrderListCard(orderTicketPage).getByTestId(responseBoxTestId);
  const boxBottom = (await responseBox.boundingBox())!;
  const tableTop = (await locateOrderListCard(orderTicketPage).getByRole('table').boundingBox())!.y;
  expect(boxBottom.y + boxBottom.height).toBeLessThanOrEqual(tableTop);
}

for (const viewportWidth of [1440, 375]) {
  test(`CA-3 at ${viewportWidth}px: the box shows "Aceita" and then "Rejeitada" with the order and the reason when PETR4 breaks the limit`, async ({ page }) => {
    await page.setViewportSize({ width: viewportWidth, height: 900 });
    await page.goto('/');
    await deleteAllOrdersOnServer(page);
    await page.reload();
    await expect(locateOrderListCard(page).getByTestId('caixa-de-resposta')).toHaveCount(0);

    const acceptedAnswer = await sendPetr4BigBuy(page);
    expect(acceptedAnswer.data.status).toBe('accepted');
    const acceptedBox = locateOrderListCard(page).getByTestId('caixa-de-resposta');
    await expect(acceptedBox).toHaveCount(1);
    await expect(acceptedBox).toBeVisible();
    await expect(acceptedBox.getByTestId('status-da-ordem')).toHaveText('Aceita');
    await expect(acceptedBox.getByTestId('ordem-da-resposta')).toHaveText('PETR4 · Compra · 99.999 × R$ 999,99');
    await expect(acceptedBox.getByTestId('mensagem-da-ordem')).toHaveText('Ordem aceita.');
    await expect(acceptedBox).toHaveCSS('border-top-color', ACCEPTED_BOX_COLORS.border);
    await expect(acceptedBox).toHaveCSS('background-color', ACCEPTED_BOX_COLORS.background);
    await expectBoxAboveTable(page, 'caixa-de-resposta');

    const rejectedAnswer = await sendPetr4BigBuy(page);
    expect(rejectedAnswer.data.status).toBe('rejected');
    const rejectedBox = locateOrderListCard(page).getByTestId('caixa-de-resposta');
    await expect(rejectedBox).toHaveCount(1);
    await expect(rejectedBox).toBeVisible();
    await expect(rejectedBox.getByTestId('status-da-ordem')).toHaveText('Rejeitada');
    await expect(rejectedBox.getByTestId('status-da-ordem')).toHaveCSS('color', REJECTED_BOX_COLORS.border);
    await expect(rejectedBox.getByTestId('ordem-da-resposta')).toHaveText('PETR4 · Compra · 99.999 × R$ 999,99');
    await expect(rejectedBox.getByTestId('ordem-da-resposta')).toHaveCSS('font-weight', '800');
    await expect(rejectedBox.getByTestId('mensagem-da-ordem')).toHaveText('Ordem rejeitada: a exposição de PETR4 passaria do limite de 100.000.000,00.');
    await expect(rejectedBox).toHaveCSS('border-top-color', REJECTED_BOX_COLORS.border);
    await expect(rejectedBox).toHaveCSS('background-color', REJECTED_BOX_COLORS.background);
    await expectBoxAboveTable(page, 'caixa-de-resposta');

    // The pill sits on the left of the order line (maquete-01), also at 375 px where the text wraps below it if needed.
    const pillBox = (await rejectedBox.getByTestId('status-da-ordem').boundingBox())!;
    const orderLineBox = (await rejectedBox.getByTestId('ordem-da-resposta').boundingBox())!;
    expect(pillBox.x).toBeLessThanOrEqual(orderLineBox.x);

    // RNF-02: no horizontal scroll of the page.
    expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBe(0);

    if (SCREENSHOT_FOLDER) {
      await locateOrderListCard(page).screenshot({ path: `${SCREENSHOT_FOLDER}/06-caixa-rejeitada-${viewportWidth}.png` });
    }
  });
}

test('RF-04: the box of the previous answer goes away as soon as a new send starts', async ({ page }) => {
  await page.goto('/');
  await deleteAllOrdersOnServer(page);
  await page.reload();
  await sendPetr4BigBuy(page);
  await expect(locateOrderListCard(page).getByTestId('caixa-de-resposta')).toBeVisible();
  // The real send is only held for 1.5 s so the instant between the click and the answer can be seen.
  await page.route('**' + CREATE_ORDER_ROUTE, async (heldOrderCreationRoute) => {
    await new Promise((releaseHeldRequest) => setTimeout(releaseHeldRequest, 1_500));
    await heldOrderCreationRoute.continue();
  });
  await page.getByLabel(/^Quantidade de/).fill('1');
  await page.getByLabel('Preço por ação (R$)').fill('10,00');
  await page.getByRole('button', { name: /^Enviar ordem/ }).click();
  await expect(locateOrderListCard(page).getByTestId('selo-enviando')).toBeVisible();
  await expect(locateOrderListCard(page).getByTestId('caixa-de-resposta')).toHaveCount(0);
  await expect(locateOrderListCard(page).getByTestId('selo-enviando')).toHaveCount(0);
  const answerOfTheSmallBuy = locateOrderListCard(page).getByTestId('caixa-de-resposta');
  await expect(answerOfTheSmallBuy.getByTestId('status-da-ordem')).toHaveText('Aceita');
  await expect(answerOfTheSmallBuy.getByTestId('ordem-da-resposta')).toHaveText('PETR4 · Compra · 1 × R$ 10,00');
});
