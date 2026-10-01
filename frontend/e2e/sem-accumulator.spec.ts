import { expect, test } from '@playwright/test';

// Roda com o OrderGenerator de pé e o OrderAccumulator DESLIGADO (CA-19).
// O cenário em .harness/validation.json para o Accumulator antes de chamar este arquivo.

test('CA-19: sem o OrderAccumulator a tela mostra erro claro em até ~5 s e continua respondendo', async ({ page }) => {
  await page.goto('/');
  await expect(page.locator('.exposicao [role="alert"]')).toHaveText(
    'Não foi possível ler a exposição no OrderAccumulator. Tente de novo em instantes.',
  );

  await page.getByLabel(/^Quantidade de/).fill('10');
  await page.getByLabel('Preço por ação (R$)').fill('10,00');
  const inicio = Date.now();
  await page.getByRole('button', { name: /^Enviar ordem/ }).click();

  await expect(page.getByTestId('status-da-ordem')).toHaveText('Erro de comunicação', { timeout: 7_000 });
  const decorrido = Date.now() - inicio;
  expect(decorrido).toBeLessThanOrEqual(6_500);
  await expect(page.getByTestId('mensagem-da-ordem')).toHaveText(
    'Não foi possível falar com o OrderAccumulator. Tente de novo em instantes.',
  );
  await expect(page.locator('.status-aceita')).toHaveCount(0);

  // A tela não congelou: o envio fica disponível de novo e os campos aceitam digitação.
  await expect(page.getByRole('button', { name: 'Enviar ordem de compra' })).toBeEnabled();
  await page.getByLabel('Preço por ação (R$)').fill('11,00');
  await expect(page.getByLabel('Preço por ação (R$)')).toHaveValue('11,00');
});
