import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

// The build goes straight to the folder the OrderGenerator serves (docs/contracts/contracts.md, section 5).
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: '../src/app-base-order-generator-webapi-ecs/wwwroot',
    emptyOutDir: true,
  },
  server: {
    proxy: { '/api': 'http://localhost:8080' },
  },
  test: {
    include: ['src/**/*.test.ts'],
  },
});
