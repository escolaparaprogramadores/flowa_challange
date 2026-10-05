import { defineConfig } from '@playwright/test';
import screenConfig from './playwright.config';

// Runs only CA-19, with the OrderGenerator up and the OrderAccumulator stopped.
export default defineConfig({ ...screenConfig, testIgnore: [], testMatch: ['**/without-accumulator.spec.ts'] });
