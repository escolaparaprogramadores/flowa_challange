import { expect, test } from '@playwright/test';

// Roda contra o OrderGenerator com o OrderAccumulator parado (CA-19). Quem roda o cenário para o
// OrderAccumulator antes e o religa depois; o teste não desliga nada sozinho.

test('CA-19: sem o OrderAccumulator a tela diz que a ordem não foi confirmada em até ~5 s e continua respondendo', async ({ page }) => {
  await page.goto('/');
  await expect(page.locator('.exposicao').getByRole('alert')).toHaveText(
    'Não foi possível ler a exposição agora. Tente de novo em instantes.',
  );

  await page.getByLabel(/^Quantidade de/).fill('10');
  await page.getByLabel('Preço por ação (R$)').fill('10,00');
  const instanteDoClique = Date.now();
  await page.getByRole('button', { name: /^Enviar ordem/ }).click();

  await expect(page.getByTestId('status-da-ordem')).toHaveText('Erro de comunicação', { timeout: 7_000 });
  const tempoAteOErroDeComunicacao = Date.now() - instanteDoClique;
  expect(tempoAteOErroDeComunicacao).toBeLessThanOrEqual(6_000);
  await expect(page.getByTestId('mensagem-da-ordem')).toHaveText(
    'A ordem não foi confirmada: o servidor de ordens não respondeu. Tente de novo em instantes.',
  );
  await expect(page.getByTestId('mensagem-da-ordem')).not.toContainText('OrderAccumulator');

  // A tela não congelou: o envio fica disponível de novo e os campos aceitam digitação.
  await expect(page.getByRole('button', { name: 'Enviar ordem de compra' })).toBeEnabled();
  await page.getByLabel('Preço por ação (R$)').fill('11,00');
  await expect(page.getByLabel('Preço por ação (R$)')).toHaveValue('11,00');
});
