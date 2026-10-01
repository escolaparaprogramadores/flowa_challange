import { defineConfig, devices } from '@playwright/test';

// A página é servida pelo OrderGenerator (porta 8080 no contrato). E2E_PORTA_DESLOCO
// desloca a porta para várias janelas rodarem na mesma máquina sem colidir.
const portaDoGenerator = 8080 + Number(process.env.E2E_PORTA_DESLOCO ?? 0);

export default defineConfig({
  testDir: './e2e',
  // O cenário com o OrderAccumulator desligado tem config própria (playwright.sem-accumulator.config.ts).
  testIgnore: ['**/sem-accumulator.spec.ts'],
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [['list'], ['json', { outputFile: 'test-results/resultado.json' }]],
  use: {
    baseURL: process.env.E2E_BASE_URL ?? `http://localhost:${portaDoGenerator}`,
    trace: 'retain-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
});
