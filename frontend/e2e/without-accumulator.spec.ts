import { expect, test } from '@playwright/test';

// Runs against the OrderGenerator with the OrderAccumulator stopped (CA-19). Whoever runs the scenario stops the
// OrderAccumulator before and starts it again after; the test does not shut anything down by itself.

test('CA-19: without the OrderAccumulator the screen says the order was not confirmed within ~5 s and keeps responding', async ({ page }) => {
  await page.goto('/');
  await expect(page.locator('.exposure').getByRole('alert')).toHaveText(
    'Não foi possível ler a exposição agora. Tente de novo em instantes.',
  );

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
