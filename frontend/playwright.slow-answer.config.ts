import { defineConfig } from '@playwright/test';
import screenConfig from './playwright.config';

// Runs only CA-11/CA-27: it pauses the Postgres container of the stack under test (E2E_POSTGRES_CONTAINER).
export default defineConfig({ ...screenConfig, testIgnore: [], testMatch: ['**/slow-answer.spec.ts'] });
