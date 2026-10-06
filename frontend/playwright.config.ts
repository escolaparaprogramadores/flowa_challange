import { defineConfig, devices } from '@playwright/test';

// The page is served by the OrderGenerator (port 8080 in the contract). E2E_PORTA_DESLOCO
// shifts the port so several windows can run on the same machine without colliding.
const generatorPort = 8080 + Number(process.env.E2E_PORTA_DESLOCO ?? 0);

export default defineConfig({
  testDir: './e2e',
  // The scenario with the OrderAccumulator stopped and the one that pauses the database have their own configs.
  testIgnore: ['**/without-accumulator.spec.ts', '**/slow-answer.spec.ts'],
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [['list'], ['json', { outputFile: 'test-results/results.json' }]],
  use: {
    baseURL: process.env.E2E_BASE_URL ?? `http://localhost:${generatorPort}`,
    trace: 'retain-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
});
