import { expect, test } from '@playwright/test';
import { formatBrazilianReais } from '../src/lib/number-format/brazilianNumberFormat';
import { EXPOSURES_ROUTE, EXPOSURE_UNAVAILABLE_MESSAGE, ORDERS_ROUTE, ORDER_LIST_UNAVAILABLE_MESSAGE } from '../src/services/ordersService';

// Runs against the OrderGenerator with the OrderAccumulator stopped (CA-19, CA-31). Whoever runs the scenario stops the
// OrderAccumulator before and starts it again after; the test does not shut anything down by itself.

type ExposuresData = { exposures: { symbol: string; exposure: number }[] };
type OrdersPageData = { total: number; pageSize: number };

test('CA-31: without the OrderAccumulator the screen shows the exposure and the orders from the database, and sending fails as before', async ({ page }) => {
  // What the database has now, read by the same OrderGenerator the screen calls.
  const exposuresAnswer = await page.request.get(EXPOSURES_ROUTE);
  expect(exposuresAnswer.status()).toBe(200);
  const storedExposures = ((await exposuresAnswer.json()) as { data: ExposuresData }).data.exposures;
  expect(storedExposures.map((storedExposure) => storedExposure.symbol)).toEqual(['PETR4', 'VALE3', 'VIIA4']);
  const ordersPageAnswer = await page.request.get(`${ORDERS_ROUTE}?page=1`);
  expect(ordersPageAnswer.status()).toBe(200);
  const storedOrdersPage = ((await ordersPageAnswer.json()) as { data: OrdersPageData }).data;

  await page.goto('/');

  for (const storedExposure of storedExposures) {
    await expect(page.getByTestId(`exposicao-${storedExposure.symbol}`).getByTestId('exposicao-atual')).toHaveText(
      formatBrazilianReais(storedExposure.exposure),
    );
  }
  await expect(page.getByText(EXPOSURE_UNAVAILABLE_MESSAGE)).toHaveCount(0);
  const orderListCard = page.getByRole('region', { name: 'Compra/Venda' });
  if (storedOrdersPage.total === 0) {
    await expect(orderListCard.getByTestId('lista-de-ordens-vazia')).toBeVisible();
  } else {
    await expect(orderListCard.getByTestId('linha-da-ordem')).toHaveCount(Math.min(storedOrdersPage.total, storedOrdersPage.pageSize));
  }
  await expect(page.getByText(ORDER_LIST_UNAVAILABLE_MESSAGE)).toHaveCount(0);

  await page.getByLabel(/^Quantidade de/).fill('10');
  await page.getByLabel('Preço por ação (R$)').fill('10,00');
  const clickInstant = Date.now();
  await page.getByRole('button', { name: /^Enviar ordem/ }).click();

  await expect(page.getByTestId('status-da-ordem')).toHaveText('Erro de comunicação', { timeout: 7_000 });
  const timeUntilCommunicationError = Date.now() - clickInstant;
  expect(timeUntilCommunicationError).toBeLessThanOrEqual(6_000);
  await expect(page.getByTestId('mensagem-da-ordem')).toHaveText(
    'A ordem não foi confirmada: o servidor de ordens não respondeu. Tente de novo em instantes.',
  );
  await expect(page.getByTestId('mensagem-da-ordem')).not.toContainText('OrderAccumulator');

  // The screen did not freeze: sending is available again and the fields accept typing.
  await expect(page.getByRole('button', { name: 'Enviar ordem de compra' })).toBeEnabled();
  await page.getByLabel('Preço por ação (R$)').fill('11,00');
  await expect(page.getByLabel('Preço por ação (R$)')).toHaveValue('11,00');
});
