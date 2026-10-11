import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

// Geliştirme sunucusu isteğe bağlıdır; API'ye yerel stack üzerinden (HTTPS, self-signed) vekillik eder.
const apiTarget = process.env.LOCA_API_ORIGIN ?? 'https://localhost:8643';

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      '/api': { target: apiTarget, secure: false, changeOrigin: false },
      '/health': { target: apiTarget, secure: false, changeOrigin: false },
    },
  },
  build: {
    outDir: 'dist',
    sourcemap: false,
    target: 'es2022',
    // CSP font-src/img-src 'self': küçük varlıklar da data: URI yerine dosya olarak çıkar.
    assetsInlineLimit: 0,
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    include: ['src/**/*.test.{ts,tsx}'],
    css: false,
    restoreMocks: true,
  },
});
