import { defineConfig } from '@playwright/test';
import configDaTela from './playwright.config';

// Roda só o CA-19, com o OrderGenerator de pé e o OrderAccumulator parado.
export default defineConfig({ ...configDaTela, testIgnore: [], testMatch: ['**/sem-accumulator.spec.ts'] });
