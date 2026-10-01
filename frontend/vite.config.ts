import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

// O build vai direto para a pasta que o OrderGenerator serve (docs/contracts/contracts.md, seção 5).
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: '../src/OrderGenerator/wwwroot',
    emptyOutDir: true,
  },
  server: {
    proxy: { '/api': 'http://localhost:8080' },
  },
  test: {
    include: ['src/**/*.test.ts'],
  },
});
